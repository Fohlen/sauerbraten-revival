using System.Collections.Generic;
using Godot;
using SauerbratenRevival.World.Physics;
using SauerbratenRevival.World.Weapons;

namespace SauerbratenRevival.World.Game;

/// <summary>
/// Encapsulates the fixed 5 ms simulation loop shared across all game world providers
/// (<see cref="Ogz.MapWorld"/>, <see cref="Editor.EmptyMap"/>, <see cref="Save.MapLevel"/>).
///
/// Drives:
/// <list type="bullet">
///   <item><description>Clock advance</description></item>
///   <item><description>Player input gathering (including mouse look) and discrete stepping</description></item>
///   <item><description>Dynent spatial cache refresh</description></item>
///   <item><description>Centralized projectile and bouncer simulation</description></item>
///   <item><description>Flag mode rules and item respawn advancement</description></item>
///   <item><description>Post-substep visual transform sync</description></item>
/// </list>
/// </summary>
public sealed class WorldSimulation
{
    private double _physicsAccum;
    private const double StepSeconds = Phys.PhysFrameTime / 1000.0;

    /// <summary>
    /// Executes fixed 5 ms physics substeps for the given game world.
    /// Call from the world node's <c>_PhysicsProcess</c>.
    /// </summary>
    public void Step(IGameWorld world, double delta)
    {
        if (Engine.IsEditorHint() || world.Octa == null || world.Physics == null) return;

        _physicsAccum += delta;
        int guard = 0;
        while (_physicsAccum >= StepSeconds && guard++ < 100)
        {
            _physicsAccum -= StepSeconds;
            world.Clock?.Advance(Phys.PhysFrameTime);

            int nowMillis = world.Clock?.NowMillis ?? 0;

            // 1. Gather player inputs (including mouse look) before stepping physics.
            for (int i = 0; i < world.Players.Count; i++)
            {
                world.Players[i].PrePhysicsStep();
            }

            // 2. Clear and update the dynent cache once per 5 ms substep.
            world.Physics.ClearDynentCache();
            foreach (var dynent in world.Physics.DynEnts)
            {
                world.Physics.UpdateDynentCache(dynent);
            }

            // 3. Step registered players.
            for (int i = 0; i < world.Players.Count; i++)
            {
                world.Players[i].Step(Phys.PhysFrameTime);
            }

            // 4. Update active projectiles (weapon.cpp:523 updateprojectiles).
            UpdateProjectiles(world, Phys.PhysFrameTime);

            // 5. Update active bouncers (weapon.cpp:231 updatebouncers).
            UpdateBouncers(world, Phys.PhysFrameTime);

            // 6. Update game mode flag rules with synchronized simulation clock.
            if (world.Flags != null && world.Mode != null && world.Mode.Has(Game.ModeFlags.Ctf))
            {
                world.Flags.Update(nowMillis);
            }

            // 7. Update item respawns with synchronized simulation clock.
            world.Items?.UpdateRespawns(Phys.PhysFrameTime);

            // 8. Post physics step for players (attack cooldowns).
            for (int i = 0; i < world.Players.Count; i++)
            {
                world.Players[i].PostPhysicsStep();
            }
        }

        // Post-substep visual sync for bouncers.
        for (int i = 0; i < world.ActiveBouncers.Count; i++)
        {
            world.ActiveBouncers[i].SyncTransform();
        }
    }

    /// <summary>
    /// Updates all active flying projectiles (rockets) in the world.
    /// Port of <c>fpsgame/weapon.cpp:523 updateprojectiles</c>.
    /// </summary>
    public static void UpdateProjectiles(IGameWorld world, int timeMillis, IReadOnlyList<IExplosionTarget>? candidates = null)
    {
        var targets = candidates ?? (IReadOnlyList<IExplosionTarget>)world.Players;
        for (int i = world.ActiveRockets.Count - 1; i >= 0; i--)
        {
            var rocket = world.ActiveRockets[i];
            if (rocket.Advance(timeMillis, world.Octa, targets))
            {
                world.ActiveRockets.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Updates all active bouncers (grenades, debris, gibs) in the world.
    /// Port of <c>fpsgame/weapon.cpp:231 updatebouncers</c>.
    /// </summary>
    public static void UpdateBouncers(IGameWorld world, int timeMillis, IReadOnlyList<IExplosionTarget>? candidates = null)
    {
        var targets = candidates ?? (IReadOnlyList<IExplosionTarget>)world.Players;
        for (int i = world.ActiveBouncers.Count - 1; i >= 0; i--)
        {
            var bouncer = world.ActiveBouncers[i];
            if (bouncer.Exploded || !GodotObject.IsInstanceValid(bouncer) || bouncer.IsQueuedForDeletion())
            {
                world.ActiveBouncers.RemoveAt(i);
                continue;
            }
            bouncer.Step(timeMillis, targets);
            if (bouncer.Exploded)
            {
                world.ActiveBouncers.RemoveAt(i);
            }
        }
    }
}
