extends RefCounted

var hill_pos := Vector3.ZERO
var scores := [0, 0]
var tick_accum := 0
var last_died_victim = null
var last_died_killer = null
var spawn_called := false
var world_ref = null

func claims_entity(entity) -> bool:
    return entity.Type == 24 # EntityType.Base

func setup_world(world, entities, root) -> void:
    world_ref = world
    for e in entities:
        if e.Type == 24:
            hill_pos = e.Position
            break

func step(world, step_millis: int, now_millis: int) -> void:
    tick_accum += step_millis

func step_player(player, now_millis: int) -> void:
    if player.IsAlive and player.Entity.FeetPos().distance_to(hill_pos) < 64.0:
        scores[player.Team - 1] += 1

func update_visual(world, last_millis: int) -> void:
    pass

func resolve_spawn(player) -> Dictionary:
    return {
        "pos": Vector3(10, 20, 30),
        "yaw": 180.0
    }

func on_player_spawn(player) -> void:
    spawn_called = true

func on_before_damage(attacker, victim, gun: int, result: Dictionary, now_millis: int) -> bool:
    # Friendly fire is completely canceled
    if attacker != null and attacker.Team == victim.Team:
        return false
    # Players on the hill receive 50% damage and have knockback neutralized
    if victim.Entity.FeetPos().distance_to(hill_pos) < 64.0:
        result.damage = int(result.damage * 0.5)
        result.push = Vector3.ZERO
    return true

func on_player_died(victim, killer, gun: int, now_millis: int) -> void:
    last_died_victim = victim
    last_died_killer = killer

func get_hud_state(local_player, now_millis: int) -> Dictionary:
    var my_team: int = local_player.Team
    var enemy_team: int = 2 if my_team == 1 else 1
    return {
        "primary_score": scores[my_team - 1],
        "secondary_score": scores[enemy_team - 1],
        "primary_is_friendly": true,
        "status_icon": 3,
        "status_counter": scores[my_team - 1]
    }
