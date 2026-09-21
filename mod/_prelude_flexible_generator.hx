//=== DiceyA11y accessibility hooks =========================================
// flexible_generator.hx runs once per run, for every generator, AFTER Rules.reset().
// That makes it the earliest and most mod-friendly place to register the global
// combat hooks. Rules.addextrascript takes INLINE hscript source (not a path),
// so each hook is a one-line runscript call.
//
// Everything below only calls runscript/Rules, so a failure here cannot corrupt
// item generation: the original generator body follows unchanged.
trace("A11Y1|boot|v=1");
runscript("diceydungeons/a11y/run");
Rules.addextrascript("runscript(\"diceydungeons/a11y/snap\",[\"startcombat\",self,target]);", "startcombat");
Rules.addextrascript("runscript(\"diceydungeons/a11y/snap\",[\"aftercombat\",self,target]);", "aftercombat");
Rules.addextrascript("runscript(\"diceydungeons/a11y/snap\",[\"beforestartturn\",self,target]);", "beforestartturn");
Rules.addextrascript("runscript(\"diceydungeons/a11y/snap\",[\"onstartturn\",self,target]);", "onstartturn");
Rules.addextrascript("runscript(\"diceydungeons/a11y/snap\",[\"endturn\",self,target]);", "endturn");
Rules.addextrascript("runscript(\"diceydungeons/a11y/snap\",[\"playerequipmentuse\",self,target]);", "playerequipmentuse");
Rules.addextrascript("runscript(\"diceydungeons/a11y/snap\",[\"enemyequipmentuse\",self,target]);", "enemyequipmentuse");
//=== end DiceyA11y ========================================================
