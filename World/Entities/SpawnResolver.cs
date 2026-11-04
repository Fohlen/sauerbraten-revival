using System;
using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Game;
using SauerbratenRevival.World.Game.Ctf;
using SauerbratenRevival.World.Game.Items;
using SauerbratenRevival.World.Game.Players;
using SauerbratenRevival.World.Physics;

namespace SauerbratenRevival.World.Entities;

/// <summary>
/// Resolves player spawn positions with full Sauerbraten engine fidelity:
/// <list type="bullet">
///   <item><description>Filters spawns by tag/team (<c>e.attr2 == tag</c>) via <c>cmode ? cmode->getspawngroup(d) : 0</c> (fpsgame/fps.cpp:327, ctf.h:1052).</description></item>
///   <item><description>Rates candidate spawns by enemy proximity and line-of-sight via <c>ratespawn()</c> and <c>proximityscore()</c> (fpsgame/fps.cpp:284-322).</description></item>
///   <item><description>Rates hold spawns by flag proximity via <c>rateholdspawn()</c> (fpsgame/ctf.h:1040).</description></item>
///   <item><description>Picks candidate spawns via weighted random selection <c>poprandomspawn()</c> (engine/world.cpp:1175).</description></item>
///   <item><description>Validates candidate spawn clearances with collision testing <c>tryspawn()</c> / <c>entinmap()</c> (engine/world.cpp:1193, physics.cpp:2022).</description></item>
/// </list>
/// </summary>
public static class SpawnResolver
{
    /// <summary>
    /// Engine <c>proximityscore(x, lower, upper)</c> (fpsgame/fps.cpp:284).
    /// Returns 1.0 if x &lt;= lower, 0.0 if x &gt;= upper, and a quadratic smooth curve between them.
    /// </summary>
    public static float ProximityScore(float x, float lower, float upper)
    {
        if (x <= lower) return 1.0f;
        if (x >= upper) return 0.0f;
        float a = x - lower, b = x - upper;
        return (b * b) / (a * a + b * b);
    }

    /// <summary>
    /// Engine <c>harmonicmean(a, b)</c> (fpsgame/fps.cpp:292).
    /// </summary>
    public static float HarmonicMean(float a, float b)
    {
        return a + b > 0 ? 2 * a * b / (a + b) : 0.0f;
    }

    /// <summary>
    /// Engine <c>rateholdspawn(e)</c> (fpsgame/ctf.h:1040).
    /// </summary>
    public static float RateHoldSpawn(Vector3 spawnPos, FlagManager? flags)
    {
        if (flags == null || flags.Flags.Count == 0) return 1.0f;
        float minFlagDist = 1e16f;
        for (int i = 0; i < flags.Flags.Count; i++)
        {
            var f = flags.Flags[i];
            if (f.SpawnIndex < 0 || (!f.Owner.HasValue && (f.DropTime == 0 || f.DropLoc.X < 0))) continue;
            Vector3 target = f.Owner.HasValue ? f.DropLoc : (f.DropTime > 0 ? f.DropLoc : f.SpawnLoc);
            float d = spawnPos.DistanceTo(target);
            if (d < minFlagDist) minFlagDist = d;
        }
        return minFlagDist < 1e16f ? ProximityScore(minFlagDist, 400.0f, 800.0f) : 1.0f;
    }

    /// <summary>
    /// Engine <c>ratespawn(d, e)</c> (fpsgame/fps.cpp:295).
    /// Evaluates spawn desirability by avoiding spawning in line-of-sight near enemies.
    /// </summary>
    public static float RateSpawn(
        MapEntity spawn,
        MapPlayer? player,
        IReadOnlyList<MapPlayer>? players,
        Octa? octa,
        GameModeInfo? mode,
        FlagManager? flags)
    {
        float eyeHeight = MapPlayer.EyeHeight;
        Vector3 loc = spawn.Position with { Z = spawn.Position.Z + eyeHeight };
        bool hasNoItems = mode?.AllowedItems == ItemSpawnMask.None;
        bool hasCmode = mode?.ObjectiveId is "ctf" or "protect" or "hold" or "rugby";
        float maxRange = !hasNoItems ? 400.0f : (hasCmode ? 300.0f : 110.0f);
        float minPlayerDist = maxRange;

        if (players != null && player != null)
        {
            for (int i = 0; i < players.Count; i++)
            {
                var o = players[i];
                if (ReferenceEquals(o, player)) continue;
                if (o.Entity.Dead) continue;
                // In team modes, ignore teammates (fps.cpp:308 isteam(o->team, p->team))
                if (mode != null && mode.TeamPlay && o.Team == player.Team && player.Team != 0) continue;

                Vector3 dir = o.Entity.O - loc;
                float distSq = dir.LengthSquared();
                if (distSq >= minPlayerDist * minPlayerDist) continue;
                float dist = Mathf.Sqrt(distSq);
                if (dist > 0f)
                {
                    // Scale distance if not in line of sight (fps.cpp:317)
                    if (octa != null)
                    {
                        Vector3 rayDir = dir / dist;
                        float losDist = OctaRaycast.RayCube(octa, loc, rayDir, dist, OctaRaycast.RayClipMat | OctaRaycast.RayPoly, 0, out _);
                        if (losDist < dist) dist *= 1.5f;
                    }
                }
                if (dist < minPlayerDist) minPlayerDist = dist;
            }
        }

        float rating = 1.0f - ProximityScore(minPlayerDist, 80.0f, maxRange);
        if (mode != null && FlagManager.IsHoldMode(mode))
        {
            float holdRating = RateHoldSpawn(spawn.Position, flags);
            return HarmonicMean(rating, holdRating);
        }
        return rating;
    }

    /// <summary>
    /// Engine <c>findplayerspawn(dynent *d, int forceent, int tag)</c> (engine/world.cpp:1198).
    /// </summary>
    public static (Vector3 Position, float Yaw) FindPlayerSpawn(
        IReadOnlyList<MapEntity>? entities,
        Vector3 fallback,
        int tag = 0,
        int forceIndex = -1,
        Random? rng = null,
        MapPlayer? player = null,
        OctaPhysics? phys = null,
        Octa? octa = null,
        GameModeInfo? mode = null,
        IReadOnlyList<MapPlayer>? players = null,
        FlagManager? flags = null)
    {
        if (entities == null || entities.Count == 0)
            return (fallback, 0f);

        // 1. Gather all ET_PLAYERSTART entities
        var allStarts = new List<MapEntity>();
        for (int i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e.Type == EntityType.PlayerStart)
                allStarts.Add(e);
        }

        if (allStarts.Count == 0)
            return (fallback, 0f);

        // If forceIndex is within range of playerstarts, try it directly (fps.cpp:326 respawnent)
        if (forceIndex >= 0 && forceIndex < allStarts.Count)
        {
            var forced = allStarts[forceIndex];
            if (TrySpawn(forced, player, phys, out var pos))
                return (pos, (float)forced.Attr1);
        }

        // 2. Filter by tag (e.attr2 == tag)
        var candidates = new List<MapEntity>();
        for (int i = 0; i < allStarts.Count; i++)
        {
            if (allStarts[i].Attr2 == tag)
                candidates.Add(allStarts[i]);
        }

        // Fallback: if tag != 0 had no matches, fallback to tag 0 (FFA spawns), then all spawns
        if (candidates.Count == 0 && tag != 0)
        {
            for (int i = 0; i < allStarts.Count; i++)
            {
                if (allStarts[i].Attr2 == 0)
                    candidates.Add(allStarts[i]);
            }
            if (candidates.Count == 0)
                candidates.AddRange(allStarts);
        }

        if (candidates.Count == 0)
            return (fallback, 0f);

        // 3. Rate each candidate spawn (world.cpp:1162 gatherspawninfos)
        rng ??= Random.Shared;
        var spawnInfos = new List<(MapEntity Entity, float Weight)>(candidates.Count);
        float total = 0f;
        for (int i = 0; i < candidates.Count; i++)
        {
            var e = candidates[i];
            float weight = RateSpawn(e, player, players, octa, mode, flags);
            spawnInfos.Add((e, weight));
            total += weight;
        }

        // 4. Pop random spawn and test collision (world.cpp:1175 poprandomspawn, tryspawn)
        while (spawnInfos.Count > 0)
        {
            int index = 0;
            if (total > 0f)
            {
                float x = (float)(rng.NextDouble() * total);
                while (x > 0 && index < spawnInfos.Count - 1)
                {
                    x -= spawnInfos[index].Weight;
                    if (x > 0) index++;
                }
            }
            else
            {
                index = rng.Next(spawnInfos.Count);
            }

            var picked = spawnInfos[index];
            total -= picked.Weight;
            spawnInfos[index] = spawnInfos[^1];
            spawnInfos.RemoveAt(spawnInfos.Count - 1);

            if (TrySpawn(picked.Entity, player, phys, out var validFeet))
            {
                return (validFeet, (float)picked.Entity.Attr1);
            }
        }

        // Fallback: if all collision checks failed, use first candidate
        return (candidates[0].Position, (float)candidates[0].Attr1);
    }

    /// <summary>
    /// Engine <c>tryspawn(dynent *d, const extentity &amp;e)</c> (engine/world.cpp:1193).
    /// </summary>
    private static bool TrySpawn(MapEntity spawn, MapPlayer? player, OctaPhysics? phys, out Vector3 spawnFeet)
    {
        spawnFeet = spawn.Position;
        if (phys == null) return true;

        var testEnt = new PhysEnt
        {
            Type = Phys.EntPlayer,
            Radius = player?.Entity.Radius ?? 4.1f,
            EyeHeight = MapPlayer.EyeHeight,
            AboveEye = player?.Entity.AboveEye ?? 1.0f,
            XRadius = player?.Entity.XRadius ?? 4.1f,
            YRadius = player?.Entity.YRadius ?? 4.1f,
            State = PhysEnt.CsAlive,
            O = spawn.Position,
            Yaw = spawn.Attr1,
        };

        if (phys.EntInMap(testEnt, avoidPlayers: true))
        {
            spawnFeet = testEnt.O with { Z = testEnt.O.Z - testEnt.EyeHeight };
            return true;
        }

        return false;
    }
}
