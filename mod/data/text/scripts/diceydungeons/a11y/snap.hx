//=== DiceyA11y: combat state emitter ======================================
// Called from every Rules.addextrascript hook with [eventName, self, target].
//
// The mod emits STATE, not prose: the companion diffs consecutive snapshots and
// decides what is worth saying. All narration logic lives in C#, where it can be
// changed without relaunching the game.
//
// Two hard constraints of this hscript dialect shaped the code below:
//   1. There is no try/catch, so one bad field reference aborts the script.
//      Fields are emitted in stages ordered by confidence; if a later stage
//      breaks, the earlier stages still reached the companion.
//   2. Function values do NOT capture enclosing script variables, so there are
//      no helper functions here at all - everything is straight-line code.
//
// Field names were read from the published Dicey Dungeons source
// (elements/Fighter.hx, Equipment.hx, Dice.hx, StatusEffect.hx), not guessed.

var ev = args[0];
var fs = [args[1], args[2]];
var whos = ["self", "target"];

trace("A11Y1|ev|" + ev);

// --- stage 1: identity and health ---------------------------------------
for(k in 0...2){
  var f = fs[k];
  // '|' separates fields and a newline would split the record, so both go.
  // StringTools is not in the script whitelist; split/join are instance methods.
  var nm = "" + f.name;
  nm = nm.split("|").join("^");
  nm = nm.split("\n").join(" ");
  var pl = "0";
  if(f.isplayer) pl = "1";
  trace("A11Y1|f|who=" + whos[k]
    + "|name=" + nm
    + "|hp=" + f.hp
    + "|maxhp=" + f.maxhp
    + "|player=" + pl);
}

// --- stage 2: run and resource stats -------------------------------------
for(k in 0...2){
  var f = fs[k];
  var cpu = "0";
  if(f.usecpuinsteadofdice) cpu = "1";
  trace("A11Y1|f2|who=" + whos[k]
    + "|lvl=" + f.level
    + "|gold=" + f.gold
    + "|dice=" + f.dice
    + "|extradice=" + f.extradice
    + "|mana=" + f.mana
    + "|maxmana=" + f.maxmana
    + "|limit=" + f.limitvalue
    + "|limitmax=" + f.limitmax
    + "|cpu=" + cpu);
}

// --- stage 3: status effects ---------------------------------------------
for(k in 0...2){
  var f = fs[k];
  var i = 0;
  for(st in f.status){
    var sn = "" + st.name;
    sn = sn.split("|").join("^");
    sn = sn.split("\n").join(" ");
    var hid = "0";
    if(st.invisible) hid = "1";
    trace("A11Y1|s|who=" + whos[k]
      + "|i=" + i
      + "|name=" + sn
      + "|value=" + st.value
      + "|type=" + st.type
      + "|hidden=" + hid);
    i++;
  }
}

// --- stage 4: equipment ---------------------------------------------------
for(k in 0...2){
  var f = fs[k];
  var i = 0;
  for(eq in f.equipment){
    var en = "" + eq.name;
    en = en.split("|").join("^");
    en = en.split("\n").join(" ");
    var used = "0";
    if(eq.usedthisbattle) used = "1";
    trace("A11Y1|e|who=" + whos[k]
      + "|i=" + i
      + "|name=" + en
      + "|mod=" + eq.namemodifier
      + "|size=" + eq.size
      + "|cd=" + eq.remainingcountdown
      + "|cdmax=" + eq.countdown
      + "|used=" + used
      + "|times=" + eq.timesused);
    i++;
  }
}

// --- stage 5: dice ---------------------------------------------------------
for(k in 0...2){
  var f = fs[k];
  var i = 0;
  for(dd in f.dicepool){
    var lk = "0";
    if(dd.locked) lk = "1";
    var fz = "0";
    if(dd.frozen) fz = "1";
    var bn = "0";
    if(dd.burn) bn = "1";
    var asg = "";
    if(dd.assigned != null) asg = "" + dd.assigned.name;
    asg = asg.split("|").join("^");
    trace("A11Y1|d|who=" + whos[k]
      + "|i=" + i
      + "|v=" + (dd.basevalue + dd.modifier)
      + "|base=" + dd.basevalue
      + "|mod=" + dd.modifier
      + "|locked=" + lk
      + "|frozen=" + fz
      + "|burn=" + bn
      + "|on=" + asg);
    i++;
  }
}

// --- stage 6: the card text a sighted player reads off the screen ---------
for(k in 0...2){
  var f = fs[k];
  var i = 0;
  for(eq in f.equipment){
    var ds = "" + eq.fulldescription;
    ds = ds.split("|").join("^");
    ds = ds.split("\n").join(" ");
    ds = ds.split("\r").join(" ");
    trace("A11Y1|ed|who=" + whos[k] + "|i=" + i + "|desc=" + ds);
    i++;
  }
}

// getcurrentfloor() is a ScriptFunctions global and was verified to work in
// combat scope; it is emitted last so an unavailable call costs nothing.
trace("A11Y1|ctx|floor=" + getcurrentfloor());

trace("A11Y1|end|" + ev);

// Emitted AFTER the end marker on purpose. `turn` is in scope in the base game's
// own addextrascript snippets, but it has not been verified in every hook here -
// and if it is missing the script aborts. Placing it last means that abort costs
// nothing: the snapshot has already been closed and delivered.
trace("A11Y1|ctx|turn=" + turn);
