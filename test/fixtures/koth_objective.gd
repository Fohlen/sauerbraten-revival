extends RefCounted

const HILL_ZONE := 0
var scores := [0, 0]
var tick_count := 0
var zone_events := 0
var last_died_victim_id := -1
var last_died_killer_id := -1
var spawn_called_for := -1

func claims_entity(entity) -> bool:
	return entity.Type == 24 # EntityType.Base

func setup_world(ctx, entities, _root) -> void:
	ctx.clear_zones()
	for e in entities:
		if e.Type == 24:
			ctx.add_sphere_zone(HILL_ZONE, e.Position, 64.0)
			break
	ctx.set_default_spawn_override(Vector3(10, 20, 30), 180.0)
	ctx.set_team_score(1, 0)
	ctx.set_team_score(2, 0)

func on_zone_event(ctx, zone_id: int, player_id: int, entered: bool, _now_millis: int) -> void:
	zone_events += 1
	if zone_id == HILL_ZONE:
		var team: int = ctx.get_player_team(player_id)
		var current_score: int = scores[team - 1] if team >= 1 and team <= 2 else 0
		if entered:
			ctx.set_player_status(player_id, 3, current_score) # HudIcon.Health = 3
		else:
			ctx.clear_player_status(player_id)

func on_objective_tick(ctx, _tick_millis: int, _now_millis: int) -> void:
	tick_count += 1
	for team in [1, 2]:
		if ctx.get_zone_team_count(HILL_ZONE, team) > 0:
			scores[team - 1] += 1
			ctx.set_team_score(team, scores[team - 1])

func on_player_spawn(_ctx, player_id: int) -> void:
	spawn_called_for = player_id

func on_damage(_ctx, dmg) -> void:
	if dmg.attacker_id >= 0 and dmg.attacker_team == dmg.victim_team:
		dmg.cancelled = true
		return
	if dmg.is_victim_in_zone(HILL_ZONE):
		dmg.damage = int(dmg.damage * 0.5)
		dmg.push = Vector3.ZERO

func on_player_died(_ctx, victim_id: int, killer_id: int, _gun: int, _now_millis: int) -> void:
	last_died_victim_id = victim_id
	last_died_killer_id = killer_id
