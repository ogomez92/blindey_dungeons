//=== DiceyA11y: run context emitter =======================================
// Runs once per run from flexible_generator.hx, in generator scope.
//
// One trace per call, ordered by confidence: there is no try/catch, so a call
// that turns out to be unavailable in this scope only costs the lines after it.
// (getfinalboss() was measured to be null here and is deliberately not used.)
trace("A11Y1|run|player=" + getplayername());
trace("A11Y1|run|floor=" + getcurrentfloor());
