extends CharacterBody3D
# Sauerbraten (Cube 2) movement MVP, ported from SVN r6889.
# Only the velocity model is ported here; see docs/movement-physics.md for the full reference.

const MAXSPEED := 100.0   # physent::maxspeed        (src/shared/ents.h:91)
const GRAVITY  := 200.0   # src/engine/physics.cpp:478
const JUMPVEL  := 125.0   # src/engine/physics.cpp:477
const SENS     := 0.0022  # mouse look, tunable

var vel := Vector3.ZERO      # physent::vel     (horizontal + jump impulse)
var falling := Vector3.ZERO  # physent::falling (gravity accumulation)
var jumping := false         # physent::jumping (set on press, consumed on ground)
var _jump_held := false

@onready var cam: Camera3D = $Camera3D

func _ready() -> void:
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED

func _unhandled_input(e: InputEvent) -> void:
	if e is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
		rotate_y(-e.relative.x * SENS)
		cam.rotation.x = clampf(cam.rotation.x - e.relative.y * SENS, -1.5533, 1.5533)
	elif e.is_action_pressed("ui_cancel"):
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE

func _physics_process(dt: float) -> void:
	var on_floor := is_on_floor()

	# jumping follows the key state but is only set on a fresh press (original
	# sets it from the jump command's press/release events, physics.cpp:2019);
	# it is consumed when applied, so holding the key does not auto-hop.
	var held := Input.is_action_pressed("jump")
	if held and not _jump_held: jumping = true
	elif not held: jumping = false
	_jump_held = held

	# vecfromyawpitch (physics.cpp:1548): move/strafe -> wish direction
	var fwd := Input.get_axis("move_back", "move_forward")
	var left := Input.get_axis("strafe_right", "strafe_left")
	var wish := Vector3.ZERO
	if fwd != 0.0 or left != 0.0:
		wish = global_transform.basis * Vector3(-left, 0.0, -fwd)
		if on_floor:  # modifyvelocity: follow the slope plane
			var n := get_floor_normal()
			wish.y = -(wish.x * n.x + wish.z * n.z) / n.y
		wish = wish.normalized()

	# modifyvelocity (physics.cpp:1589)
	var target := wish * MAXSPEED
	target *= 1.3 if fwd != 0.0 and left == 0.0 else 1.0  # forward-only bonus
	target *= 1.3 if not on_floor else 1.0                # airborne bonus
	var fric := 6.0 if on_floor else 30.0
	vel = target + (vel - target) * pow(1.0 - 1.0 / fric, dt / 0.02)

	# modifygravity (physics.cpp:1650) + jump (physics.cpp:1603)
	if on_floor:
		falling = Vector3.ZERO
		if jumping:
			jumping = false
			vel.y = maxf(vel.y, JUMPVEL)
	else:
		falling.y -= GRAVITY * dt

	velocity = vel + falling
	move_and_slide()

	# keep vel/falling consistent with what the collision solver actually did
	vel.x = velocity.x
	vel.z = velocity.z
	if is_on_floor() and velocity.y <= 0.0:
		vel.y = 0.0
		falling = Vector3.ZERO
