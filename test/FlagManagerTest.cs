using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using SauerbratenRevival.Tests;
using SauerbratenRevival.World.Entities;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Ogz;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.Test;

public class FlagManagerTest : TestClass
{
    public FlagManagerTest(Node testScene) : base(testScene) { }

    private static GameModeInfo GetMode(string id) => GameModeCatalog.Get(id);

    [Test]
    public void AddFlagAndHoldSpawnsBasicSetup()
    {
        var fm = new FlagManager();
        TestAssert.True(fm.AddFlag(0, new Vector3(10, 20, 30), 1), "add flag 0");
        TestAssert.True(fm.AddFlag(1, new Vector3(40, 50, 60), 2), "add flag 1");
        TestAssert.Equal(2, fm.Flags.Count, "flags count");

        TestAssert.Equal(1, fm.Flags[0].Team, "flag 0 team");
        TestAssert.Equal(new Vector3(10, 20, 30), fm.Flags[0].SpawnLoc, "flag 0 spawn");
        TestAssert.Equal(2, fm.Flags[1].Team, "flag 1 team");

        TestAssert.True(fm.AddHoldSpawn(new Vector3(100, 100, 0)), "add hold spawn 0");
        TestAssert.True(fm.AddHoldSpawn(new Vector3(200, 200, 0)), "add hold spawn 1");
        TestAssert.Equal(2, fm.HoldSpawns.Count, "hold spawns count");
    }

    [Test]
    public void CtfPickUpEnemyFlagAndReturnFriendlyFlag()
    {
        var fm = new FlagManager();
        var ctfMode = GetMode("ctf");
        var audioEvents = new List<FlagAudioEvent>();
        fm.OnAudioEvent += audioEvents.Add;

        fm.AddFlag(0, new Vector3(0, 0, 0), 1); // Blue flag
        fm.AddFlag(1, new Vector3(100, 0, 0), 2); // Red flag

        int p1 = 1, p1Team = 1; // Blue player
        int p2 = 2, p2Team = 2; // Red player

        // Player 1 cannot take own flag if it's at base and no enemy flag is held
        bool takenOwn = fm.TakeFlag(p1, p1Team, Vector3.Zero, 0, fm.Flags[0].Version, ctfMode, 1000);
        TestAssert.False(takenOwn, "blue player cannot steal own flag at base");

        // Player 1 steals enemy flag (flag 1)
        int f1Ver = fm.Flags[1].Version;
        bool takenEnemy = fm.TakeFlag(p1, p1Team, new Vector3(100, 0, 0), 1, f1Ver, ctfMode, 1000);
        TestAssert.True(takenEnemy, "blue player steals enemy flag");
        TestAssert.Equal(p1, fm.Flags[1].Owner, "flag 1 owner is p1");
        TestAssert.Equal(f1Ver + 1, fm.Flags[1].Version, "flag 1 version bumped");
        TestAssert.Equal(1, audioEvents.Count, "audio event fired");
        TestAssert.Equal(GameSound.FlagPickup, audioEvents[0].Sound, "pickup sound played");

        // Player 2 steals blue flag (flag 0)
        int f0Ver = fm.Flags[0].Version;
        TestAssert.True(fm.TakeFlag(p2, p2Team, Vector3.Zero, 0, f0Ver, ctfMode, 1050), "red player steals flag 0");

        // Player 2 drops blue flag at (50, 0, 0)
        fm.DropPlayerFlag(p2, new Vector3(50, 0, 0), ctfMode, 2000);
        TestAssert.False(fm.Flags[0].HasOwner, "flag 0 has no owner after drop");
        TestAssert.Equal(new Vector3(50, 0, 0), fm.Flags[0].DropLoc, "flag 0 droploc");
        TestAssert.Equal(2000, fm.Flags[0].DropTime, "flag 0 droptime");
        TestAssert.Equal(GameSound.FlagDrop, audioEvents[^1].Sound, "flag drop sound");

        // While friendly flag 0 is dropped, Player 1 touches base flag 0 -> cannot capture because flag 0 is not home!
        bool capWhileDropped = fm.TakeFlag(p1, p1Team, Vector3.Zero, 0, fm.Flags[0].Version, ctfMode, 2100);
        TestAssert.False(capWhileDropped, "cannot capture while friendly flag is not at base");

        // Another friendly player (or p1) touches dropped friendly flag 0 -> returns it!
        f0Ver = fm.Flags[0].Version;
        bool returned = fm.TakeFlag(p1, p1Team, new Vector3(50, 0, 0), 0, f0Ver, ctfMode, 2200);
        TestAssert.True(returned, "touching dropped friendly flag returns it");
        TestAssert.Equal(0, fm.Flags[0].DropTime, "flag 0 returned: droptime 0");
        TestAssert.Equal(GameSound.FlagReturn, audioEvents[^1].Sound, "return sound");

        // Now friendly flag 0 is home! Player 1 touches flag 0 to capture!
        f0Ver = fm.Flags[0].Version;
        f1Ver = fm.Flags[1].Version;
        int playerScoreCount = 0;
        bool captured = fm.TakeFlag(p1, p1Team, Vector3.Zero, 0, f0Ver, ctfMode, 2300, (pid, score) =>
        {
            if (pid == p1) playerScoreCount += score;
        });

        TestAssert.True(captured, "p1 captures enemy flag at home base");
        TestAssert.Equal(1, fm.GetScore(1), "blue team score is 1");
        TestAssert.Equal(1, playerScoreCount, "player 1 score incremented");
        TestAssert.False(fm.Flags[1].HasOwner, "captured flag is returned/unowned");
        TestAssert.Equal(GameSound.FlagScore, audioEvents[^1].Sound, "score sound played");
    }

    [Test]
    public void CtfDropTimeoutResetsFlag()
    {
        var fm = new FlagManager();
        var ctfMode = GetMode("ctf");
        var audioEvents = new List<FlagAudioEvent>();
        fm.OnAudioEvent += audioEvents.Add;

        fm.AddFlag(0, new Vector3(0, 0, 0), 1);
        fm.TakeFlag(2, 2, Vector3.Zero, 0, fm.Flags[0].Version, ctfMode, 1000);
        fm.DropPlayerFlag(2, new Vector3(20, 20, 0), ctfMode, 1500);

        // Advance 5 seconds - should not reset yet (RESETFLAGTIME = 10000)
        fm.Update(ctfMode, 6500);
        TestAssert.True(fm.Flags[0].DropTime > 0, "flag still dropped at 5s");

        // Advance to 10 seconds after drop (11500ms)
        fm.Update(ctfMode, 11500);
        TestAssert.Equal(0, fm.Flags[0].DropTime, "flag reset after 10s");
        TestAssert.Equal(GameSound.FlagReset, audioEvents[^1].Sound, "reset sound played");
        TestAssert.Equal(0, fm.GetScore(1), "no team penalty in normal CTF");
    }

    [Test]
    public void ProtectModeRules()
    {
        var fm = new FlagManager();
        var protectMode = GetMode("protect");
        var audioEvents = new List<FlagAudioEvent>();
        fm.OnAudioEvent += audioEvents.Add;

        fm.AddFlag(0, new Vector3(0, 0, 0), 1, 0, true);   // Blue flag
        fm.AddFlag(1, new Vector3(100, 0, 0), 2, 0, true); // Red flag

        int p1 = 1, p1Team = 1; // Blue player
        int p2 = 2, p2Team = 2; // Red player

        // In Protect mode: player picks up / protects FRIENDLY flag!
        bool tookFriendly = fm.TakeFlag(p1, p1Team, Vector3.Zero, 0, fm.Flags[0].Version, protectMode, 1000);
        TestAssert.True(tookFriendly, "player 1 picks up friendly flag in protect mode");
        TestAssert.Equal(p1, fm.Flags[0].Owner, "flag 0 owner is p1");

        // Player 1 drops friendly flag inside own base -> immediately returns
        fm.DropPlayerFlag(p1, new Vector3(10, 10, 0), protectMode, 1200);
        TestAssert.False(fm.Flags[0].HasOwner, "flag 0 no owner");
        TestAssert.Equal(0, fm.Flags[0].DropTime, "returned immediately because inside base");
        TestAssert.Equal(GameSound.FlagReturn, audioEvents[^1].Sound, "flag returned sound");

        // Player 1 picks up friendly flag again and drops outside base
        fm.TakeFlag(p1, p1Team, Vector3.Zero, 0, fm.Flags[0].Version, protectMode, 1300);
        fm.DropPlayerFlag(p1, new Vector3(200, 200, 0), protectMode, 1400);
        TestAssert.True(fm.Flags[0].DropTime > 0, "flag dropped outside base");

        // If friendly dropped flag resets in Protect mode, team receives -1 penalty!
        fm.Update(protectMode, 1400 + FlagManager.ResetFlagTime);
        TestAssert.Equal(0, fm.Flags[0].DropTime, "flag reset");
        TestAssert.Equal(-1, fm.GetScore(1), "blue team receives -1 penalty on dropped flag reset");
        TestAssert.Equal(GameSound.FlagReset, audioEvents[^1].Sound, "reset sound");

        // Advance time past InvisFlagTime (20s) so flag is visible again
        fm.Update(protectMode, 1400 + FlagManager.ResetFlagTime + FlagManager.InvisFlagTime);

        // Touch enemy flag to score and reset it
        int p2Scores = 0;
        int currentVersion = fm.Flags[0].Version;
        bool p2Scored = fm.TakeFlag(p2, p2Team, Vector3.Zero, 0, currentVersion, protectMode, 35000, (pid, score) => p2Scores += score);
        TestAssert.True(p2Scored, "p2 touched enemy flag to score");
        TestAssert.Equal(1, fm.GetScore(2), "red team scores +1");
        TestAssert.Equal(1, p2Scores, "p2 player score +1");
        TestAssert.Equal(GameSound.FlagScore, audioEvents[^1].Sound, "score sound");
    }

    [Test]
    public void HoldModeRulesAndCountdown()
    {
        var fm = new FlagManager();
        var holdMode = GetMode("hold");
        var audioEvents = new List<FlagAudioEvent>();
        fm.OnAudioEvent += audioEvents.Add;

        fm.AddHoldSpawn(new Vector3(50, 50, 0));
        fm.AddHoldSpawn(new Vector3(150, 150, 0));
        fm.SetupHoldSpawns();

        TestAssert.Equal(1, fm.Flags.Count, "single neutral flag in hold mode");
        var f = fm.Flags[0];
        TestAssert.Equal(0, f.Team, "neutral flag initially team 0");

        int p1 = 1, p1Team = 1; // Blue player

        // Player 1 picks up neutral flag
        bool taken = fm.TakeFlag(p1, p1Team, f.SpawnLoc, 0, f.Version, holdMode, 1000);
        TestAssert.True(taken, "p1 takes hold flag");
        TestAssert.Equal(p1, f.Owner, "p1 is carrier");
        TestAssert.Equal(1, f.Team, "flag is now blue team");
        TestAssert.Equal(1000, f.HoldTime, "holdtime set to 1000");

        // Update at 10 seconds of holding: counter is 10s, no score yet
        fm.Update(holdMode, 11000);
        TestAssert.Equal(10000, f.HoldCounter(11000), "held 10s");
        TestAssert.Equal(0, fm.GetScore(1), "not scored yet at 10s");

        // Player dies while holding: hold death penalty (5s added to holdtime)
        fm.OnPlayerDied(p1, null, false, new Vector3(60, 60, 0), holdMode, 12000);
        TestAssert.False(f.HasOwner, "f dropped on death");
        // Prior to death, holdtime was 1000. 5s penalty adds 5000 -> 6000.
        // At droptime 12000, counter is (12000 - 6000) = 6000 (6s credited, lost 5s).
        TestAssert.Equal(6000, f.HoldTime, "hold time incremented by 5s death penalty");

        // Player 1 picks flag back up at 14000ms:
        // ctf.h ownflag: if(droptime < owntime) holdtime += 2*(owntime - droptime)
        // 2 * (14000 - 12000) = 4000ms penalty added for drop time!
        // holdtime becomes 6000 + 4000 = 10000.
        fm.TakeFlag(p1, p1Team, f.DropLoc, 0, f.Version, holdMode, 14000);
        TestAssert.Equal(10000, f.HoldTime, "holdtime adjusted on pickup");

        // Hold until 20s completed: holdtime = 10000, reaches 20s at 30000ms
        int p1Score = 0;
        fm.Update(holdMode, 30000, pid => (p1Team, Vector3.Zero), (pid, s) => p1Score += s);

        TestAssert.Equal(1, fm.GetScore(1), "blue team scored after 20s hold");
        TestAssert.Equal(1, p1Score, "p1 scored after 20s hold");
        TestAssert.False(f.HasOwner, "flag returned/respawned after score");
        TestAssert.Equal(GameSound.FlagScore, audioEvents[^1].Sound, "score sound played");

        // Drop timeout for Hold mode: 30s timeout resets flag to next spawn
        fm.TakeFlag(p1, p1Team, f.SpawnLoc, 0, f.Version, holdMode, 31000);
        fm.DropPlayerFlag(p1, new Vector3(80, 80, 0), holdMode, 32000);
        fm.Update(holdMode, 32000 + FlagManager.HoldResetFlagTime);
        TestAssert.Equal(0, f.DropTime, "flag reset on 30s drop timeout");
        TestAssert.Equal(GameSound.FlagReset, audioEvents[^1].Sound, "reset sound");
    }

    [Test]
    public void MapPlayerCheckFlagsProximityAndDebounce()
    {
        var player = new MapPlayer
        {
            PlayerId = 1,
            Team = 1, // Blue team
            Mode = GetMode("ctf")
        };
        player.Entity.O = new Vector3(0, 0, 14); // Eye position, feet is at (0, 0, 0)
        player.Entity.State = PhysEnt.CsAlive;

        var fm = new FlagManager();
        player.Flags = fm;

        // Flag 0: Blue flag at (0, 0, 0)
        // Flag 1: Red flag at (100, 0, 0)
        fm.AddFlag(0, new Vector3(0, 0, 0), 1, 0, false);
        fm.AddFlag(1, new Vector3(100, 0, 0), 2, 0, false);

        // Player is at (0, 0, 0) - friendly flag is at (0, 0, 0), but player is not carrying red flag, so no capture
        player.CheckFlags(1000);
        TestAssert.False(fm.Flags[0].HasOwner, "friendly flag not taken");
        TestAssert.False(fm.Flags[1].HasOwner, "enemy flag not taken yet");

        // Move player close to red flag (100, 0, 0) within FlagRadius (16)
        player.Entity.O = new Vector3(95, 0, 14);
        player.CheckFlags(1100);

        TestAssert.True(fm.Flags[1].HasOwner, "red flag taken by blue player");
        TestAssert.Equal(player.PlayerId, fm.Flags[1].Owner, "carrier is blue player");

        // Calling CheckFlags again while in radius should debounce
        player.CheckFlags(1200);
        TestAssert.Equal(player.PlayerId, fm.Flags[1].Owner, "flag still owned");

        // Move player back to friendly base (0, 0, 0) to capture!
        player.Entity.O = new Vector3(5, 0, 14);
        player.CheckFlags(2000);

        TestAssert.Equal(1, fm.GetScore(1), "blue team scored capture");
        TestAssert.False(fm.Flags[1].HasOwner, "red flag returned home after capture");
        player.Dispose();
    }

    [Test]
    public void FlagNodeVisualUpdates()
    {
        var node = new FlagNode
        {
            FlagId = 0,
            Team = 2 // Red flag
        };

        var flag = new FlagState
        {
            Id = 0,
            Team = 2,
            SpawnLoc = new Vector3(50, 60, 10),
            SpawnAngle = 90f
        };

        var ctfMode = GetMode("ctf");

        // Local player is team 1 (blue). Enemy flag (red) should select flags/red
        node.UpdateVisual(flag, ctfMode, 1000, localPlayerTeam: 1);

        TestAssert.True(node.Visible, "node is visible");
        TestAssert.Equal(flag.SpawnLoc, node.Position, "node position matches flag position");
        node.Dispose();
    }
}
