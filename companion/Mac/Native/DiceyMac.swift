// DiceyMac: the macOS services the companion needs, as a C ABI for P/Invoke.
//
//   speech    AVSpeechSynthesizer, with rate and voice
//   keyboard  a CGEventTap on its own run loop thread: the Mac counterpart of the
//             Windows low-level keyboard hook (needs the Accessibility permission)
//   focus     the process id of the frontmost window, to scope hotkeys to the game
//
// Built by companion/Mac/build_native.sh into libDiceyMac.dylib (arm64 + x86_64).

import AVFoundation
import ApplicationServices
import CoreGraphics
import Foundation

// MARK: - Speech

/// DICEYA11Y_SPEECH_TRACE=1 prints each utterance as the synthesizer really starts it (a
/// "Spoke:" log line only means it was handed over). Delegates arrive on the main run loop,
/// so no trace at all means that loop is not being run.
private final class Trace: NSObject, AVSpeechSynthesizerDelegate {
    func speechSynthesizer(_ s: AVSpeechSynthesizer, didStart u: AVSpeechUtterance) {
        FileHandle.standardError.write("AVSpeech started: \(u.speechString)\n".data(using: .utf8)!)
    }
}

private final class Speaker {
    let synth = AVSpeechSynthesizer()
    var rate: Float = AVSpeechUtteranceDefaultSpeechRate
    var voice: AVSpeechSynthesisVoice?
    let trace: Trace? = ProcessInfo.processInfo.environment["DICEYA11Y_SPEECH_TRACE"] == "1" ? Trace() : nil

    init() { synth.delegate = trace }

    func speak(_ text: String, interrupt: Bool) {
        if interrupt && synth.isSpeaking { synth.stopSpeaking(at: .immediate) }
        let u = AVSpeechUtterance(string: text)
        u.rate = rate
        if let voice { u.voice = voice }
        synth.speak(u)
    }
}

private var speaker: Speaker?

@_cdecl("dm_speech_create")
public func dm_speech_create() -> Int32 {
    if speaker == nil { speaker = Speaker() }
    return 1
}

@_cdecl("dm_speech_speak")
public func dm_speech_speak(_ text: UnsafePointer<CChar>, _ interrupt: Bool) -> Int32 {
    guard let speaker else { return 0 }
    speaker.speak(String(cString: text), interrupt: interrupt)
    return 1
}

@_cdecl("dm_speech_stop")
public func dm_speech_stop() {
    speaker?.synth.stopSpeaking(at: .immediate)
}

/// 0...1 over the synthesizer's whole range; 0.5 is the system's normal rate.
@_cdecl("dm_speech_set_rate")
public func dm_speech_set_rate(_ fraction: Float) {
    let f = min(max(fraction, 0), 1)
    let lo = AVSpeechUtteranceMinimumSpeechRate, hi = AVSpeechUtteranceMaximumSpeechRate
    speaker?.rate = lo + (hi - lo) * f
}

/// A voice identifier, or a voice name ("Samantha"), or a language ("es-ES"); empty for
/// the system default. Returns 0 when nothing matched (the default is kept).
@_cdecl("dm_speech_set_voice")
public func dm_speech_set_voice(_ name: UnsafePointer<CChar>) -> Int32 {
    let wanted = String(cString: name).trimmingCharacters(in: .whitespaces)
    if wanted.isEmpty { speaker?.voice = nil; return 1 }
    let voices = AVSpeechSynthesisVoice.speechVoices()
    let match = AVSpeechSynthesisVoice(identifier: wanted)
        ?? voices.first { $0.name.caseInsensitiveCompare(wanted) == .orderedSame }
        ?? AVSpeechSynthesisVoice(language: wanted)
    guard let match else { return 0 }
    speaker?.voice = match
    return 1
}

// MARK: - Main run loop
//
// AVSpeechSynthesizer finishes each utterance, and starts the next, through the main
// queue. With the main thread blocked (the companion just waits for the game to exit) the
// first utterance plays and every later one is silently dropped - measured. So on macOS the
// main thread runs this loop instead of blocking.

private var mainLoopStop = false

/// Runs the main run loop on the calling thread (must be the main thread) until
/// dm_main_loop_stop, or for `seconds` when that is positive.
@_cdecl("dm_main_loop_run")
public func dm_main_loop_run(_ seconds: Double) {
    let until = seconds > 0 ? Date().addingTimeInterval(seconds) : Date.distantFuture
    while !mainLoopStop && Date() < until {
        CFRunLoopRunInMode(.defaultMode, 0.25, false)
    }
}

@_cdecl("dm_main_loop_stop")
public func dm_main_loop_stop() {
    mainLoopStop = true
}

// MARK: - Keyboard

/// Called for every key down / up. Return nonzero to swallow the event.
public typealias KeyCallback = @convention(c) (Int32, Int32, UInt64, Int32) -> Int32

private var keyCallback: KeyCallback?
private var tap: CFMachPort?
private var tapLoop: CFRunLoop?

@_cdecl("dm_accessibility_trusted")
public func dm_accessibility_trusted(_ prompt: Bool) -> Bool {
    let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
    return AXIsProcessTrustedWithOptions([key: prompt] as CFDictionary)
}

/// Starts the tap on its own thread. 1 = running, 0 = could not be created (no
/// Accessibility permission).
/// Whether the modifier that raised a flagsChanged event is now held, by its own flag.
private func modifierIsDown(_ keycode: Int32, _ flags: CGEventFlags) -> Bool {
    switch keycode {
    case 56, 60: return flags.contains(.maskShift)         // Shift left / right
    case 59, 62: return flags.contains(.maskControl)       // Control left / right
    case 58, 61: return flags.contains(.maskAlternate)     // Option left / right
    case 54, 55: return flags.contains(.maskCommand)       // Command right / left
    case 57:     return flags.contains(.maskAlphaShift)    // Caps Lock
    default:     return false
    }
}

@_cdecl("dm_keytap_start")
public func dm_keytap_start(_ callback: KeyCallback) -> Int32 {
    keyCallback = callback
    // flagsChanged too: a modifier on its own (Control, to stop speech) is never a keyDown.
    let mask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue)
             | (1 << CGEventType.flagsChanged.rawValue)
    guard let port = CGEvent.tapCreate(
        tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
        eventsOfInterest: CGEventMask(mask),
        callback: { _, type, event, _ in
            // The system turns a tap off when it is slow; turn it straight back on.
            if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
                return Unmanaged.passUnretained(event)
            }
            guard let cb = keyCallback,
                  type == .keyDown || type == .keyUp || type == .flagsChanged else {
                return Unmanaged.passUnretained(event)
            }
            let code = Int32(event.getIntegerValueField(.keyboardEventKeycode))
            let rep = Int32(event.getIntegerValueField(.keyboardEventAutorepeat))
            // flagsChanged carries no direction: a modifier is down when its own flag is
            // now set. An unknown modifier reports up, which the hook simply ignores.
            let down: Int32
            if type == .flagsChanged {
                down = modifierIsDown(code, event.flags) ? 1 : 0
            } else {
                down = type == .keyDown ? 1 : 0
            }
            let swallow = cb(code, down, event.flags.rawValue, rep)
            // A modifier is never swallowed: the system needs it for its own chords.
            if type == .flagsChanged { return Unmanaged.passUnretained(event) }
            return swallow != 0 ? nil : Unmanaged.passUnretained(event)
        },
        userInfo: nil)
    else { return 0 }

    tap = port
    let ready = DispatchSemaphore(value: 0)
    let thread = Thread {
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, port, 0)
        tapLoop = CFRunLoopGetCurrent()
        CFRunLoopAddSource(tapLoop, source, .commonModes)
        CGEvent.tapEnable(tap: port, enable: true)
        ready.signal()
        CFRunLoopRun()
    }
    thread.name = "DiceyA11y key tap"
    thread.start()
    ready.wait()
    return 1
}

@_cdecl("dm_keytap_stop")
public func dm_keytap_stop() {
    if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
    if let tapLoop { CFRunLoopStop(tapLoop) }
    tap = nil
    tapLoop = nil
}

// MARK: - Focus

/// Owner of the frontmost normal window (layer 0), or 0. Works without an app run loop,
/// unlike NSWorkspace.frontmostApplication, which never updates in a process that has none.
@_cdecl("dm_frontmost_pid")
public func dm_frontmost_pid() -> Int32 {
    guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
            as? [[String: Any]] else { return 0 }
    for w in list where (w[kCGWindowLayer as String] as? Int) == 0 {
        if let pid = w[kCGWindowOwnerPID as String] as? Int32 { return pid }
        if let pid = w[kCGWindowOwnerPID as String] as? Int { return Int32(pid) }
    }
    return 0
}
