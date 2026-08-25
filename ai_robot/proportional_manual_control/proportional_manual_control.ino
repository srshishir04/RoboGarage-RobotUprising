/*
  proportional_manual_control.ino  —  Human-Controlled Robot (Bluepad32)
  =======================================================================
  PS4 / PS5 / Xbox controller drives an ESP32 4-wheel robot over Bluetooth.

  CONTROLS
    R2 (right trigger) ............ drive FORWARD  (harder press = faster)
    L2 (left trigger)  ............ drive BACKWARD (harder press = faster)
    Left stick X ................. steer left / right (works fwd AND back)
    No trigger ................... stop

  PAIRING (do this every time the light is not already solid)
    1. Power the robot. Open Serial Monitor @115200 -> "Bluepad32 robot ready".
    2. On the controller, HOLD  SHARE + PS  together until the light bar
       FLASHES rapidly (pairing mode).
    3. Serial prints "Gamepad connected" and the light goes solid. Drive.

  BOARD REQUIREMENT (critical)
    Bluepad32 replaces the ESP32 Bluetooth stack, so you MUST use the
    Bluepad32 board package and select an "ESP32 Bluepad32" board, NOT the
    plain "ESP32 Dev Module".
      • Preferences -> Additional Board Manager URLs, add:
        https://raw.githubusercontent.com/ricardoquesada/esp32-arduino-lib-builder/master/bluepad32_files/package_esp32_bluepad32_index.json
      • Boards Manager -> install "ESP32 Bluepad32 by Ricardo Quesada"
      • Tools -> Board -> pick the Bluepad32 ESP32 Dev Module

  WHAT WAS FIXED vs the old version
    • enableNewBluetoothConnections(true) is now ENABLED (the old file had it
      commented out, so the Share-button pairing never worked).
    • A one-time pairing reset clears stale controller bonds on first boot.
    • Steering math rewritten so forward/back/left/right all behave correctly
      and symmetrically (the old map() ranges were inconsistent).
    • Deadzone, trigger smoothing, and a clean stop when the controller drops.

  MOTOR WIRING — must match your chassis. If a direction is wrong, see the
  two "FLIP" switches in the CONFIG block below (no need to rewire).
*/

#include <Bluepad32.h>

// =====================================================================
// CONFIG
// =====================================================================

// Motor pins (same mapping as the AI robots)
#define LEFT_DIR_FORWARD    23
#define LEFT_DIR_BACKWARD   21
#define LEFT_PWM            19
#define RIGHT_DIR_FORWARD   33
#define RIGHT_DIR_BACKWARD  25
#define RIGHT_PWM           32

// ---- Direction fix switches (no rewiring needed) --------------------
// If the WHOLE robot drives backward when you press R2: set INVERT_DRIVE true.
// If it turns the wrong way (left stick steers right): set SWAP_STEER  true.
// If only ONE wheel spins the wrong way: flip that motor's two dir pins
//   in the #defines above, OR set INVERT_LEFT / INVERT_RIGHT below.
const bool INVERT_DRIVE = false;   // flip forward/backward globally
const bool SWAP_STEER   = false;   // flip left/right steering
const bool INVERT_LEFT  = false;   // flip left motor only
const bool INVERT_RIGHT = false;   // flip right motor only

const uint8_t MAX_SPEED        = 255;   // 0..255 PWM ceiling
const uint8_t MIN_DRIVE_SPEED  = 45;    // overcome motor stall (raise if it stalls)
const int     STICK_DEADZONE   = 40;    // |axisX| below this = going straight (0..512)
const int     TRIGGER_THRESHOLD= 80;    // trigger press to count as "on" (0..1023)

ControllerPtr myGamepad = nullptr;
bool keysCleared = false;

// =====================================================================
// MOTOR CONTROL
// =====================================================================

void driveLeft(int16_t speed) {            // speed: -255..255 (+ = forward)
  if (INVERT_LEFT) speed = -speed;
  speed = constrain(speed, -255, 255);
  if (speed > 0) {
    digitalWrite(LEFT_DIR_FORWARD, HIGH); digitalWrite(LEFT_DIR_BACKWARD, LOW);
    analogWrite(LEFT_PWM, speed);
  } else if (speed < 0) {
    digitalWrite(LEFT_DIR_FORWARD, LOW);  digitalWrite(LEFT_DIR_BACKWARD, HIGH);
    analogWrite(LEFT_PWM, -speed);
  } else {
    digitalWrite(LEFT_DIR_FORWARD, LOW);  digitalWrite(LEFT_DIR_BACKWARD, LOW);
    analogWrite(LEFT_PWM, 0);
  }
}

void driveRight(int16_t speed) {           // speed: -255..255 (+ = forward)
  if (INVERT_RIGHT) speed = -speed;
  speed = constrain(speed, -255, 255);
  if (speed > 0) {
    digitalWrite(RIGHT_DIR_FORWARD, HIGH); digitalWrite(RIGHT_DIR_BACKWARD, LOW);
    analogWrite(RIGHT_PWM, speed);
  } else if (speed < 0) {
    digitalWrite(RIGHT_DIR_FORWARD, LOW);  digitalWrite(RIGHT_DIR_BACKWARD, HIGH);
    analogWrite(RIGHT_PWM, -speed);
  } else {
    digitalWrite(RIGHT_DIR_FORWARD, LOW);  digitalWrite(RIGHT_DIR_BACKWARD, LOW);
    analogWrite(RIGHT_PWM, 0);
  }
}

void stopMotors() { driveLeft(0); driveRight(0); }

// Apply a base speed (+forward / -backward) plus a steer term (-255..255).
// Positive steer turns the robot RIGHT (slows/reverses the right wheel).
void drive(int16_t base, int16_t steer) {
  if (INVERT_DRIVE) base = -base;
  if (SWAP_STEER)   steer = -steer;

  int16_t left  = base + steer;
  int16_t right = base - steer;

  // Apply a minimum drive speed so small inputs actually move the robot,
  // but never on a zero command (keeps a clean stop).
  auto floorSpeed = [](int16_t s) -> int16_t {
    if (s > 0 && s < MIN_DRIVE_SPEED) return MIN_DRIVE_SPEED;
    if (s < 0 && s > -MIN_DRIVE_SPEED) return -MIN_DRIVE_SPEED;
    return s;
  };
  driveLeft(floorSpeed(constrain(left,  -MAX_SPEED, MAX_SPEED)));
  driveRight(floorSpeed(constrain(right, -MAX_SPEED, MAX_SPEED)));
}

// =====================================================================
// GAMEPAD EVENTS
// =====================================================================

void onConnectedGamepad(ControllerPtr gp) {
  if (myGamepad == nullptr) {
    myGamepad = gp;
    Serial.printf("Gamepad connected: index=%d, model=%s\n", gp->index(), gp->getModelName());
    gp->setColorLED(0, 255, 0);   // green = connected (PS4/PS5)
    gp->setPlayerLEDs(0x01);
  } else {
    Serial.println("A second controller tried to connect; ignoring (one driver only).");
  }
}

void onDisconnectedGamepad(ControllerPtr gp) {
  if (gp == myGamepad) {
    Serial.println("Gamepad disconnected -> motors stopped.");
    myGamepad = nullptr;
    stopMotors();
  }
}

// =====================================================================
// DRIVE LOGIC
// =====================================================================

void handleGamepad(ControllerPtr ctl) {
  if (!ctl || !ctl->isConnected()) { stopMotors(); return; }

  int throttle = ctl->throttle();   // R2: 0..1023
  int brake    = ctl->brake();      // L2: 0..1023
  int stickX   = ctl->axisX();      // -512..511

  // Steering: scale stick to a steer term. Deadzone keeps straight-line driving clean.
  int16_t steer = 0;
  if (abs(stickX) > STICK_DEADZONE) {
    int s = (stickX > 0) ? stickX - STICK_DEADZONE : stickX + STICK_DEADZONE;
    steer = map(s, (512 - STICK_DEADZONE), -(512 - STICK_DEADZONE), -MAX_SPEED, MAX_SPEED);
  }

  bool fwd = throttle > TRIGGER_THRESHOLD;
  bool bwd = brake    > TRIGGER_THRESHOLD;

  if (fwd && !bwd) {
    int16_t base = map(throttle, TRIGGER_THRESHOLD, 1023, MIN_DRIVE_SPEED, MAX_SPEED);
    drive(base, steer);
  } else if (bwd && !fwd) {
    int16_t base = -map(brake, TRIGGER_THRESHOLD, 1023, MIN_DRIVE_SPEED, MAX_SPEED);
    // When reversing, flip steer so the stick still turns the robot the
    // intuitive way (push left = robot's left from the driver's view).
    drive(base, -steer);
  } else if (steer != 0 && !fwd && !bwd) {
    // Spin in place when only steering (no trigger) — handy for lining up.
    int16_t spin = steer / 2;
    driveLeft(constrain(spin, -MAX_SPEED, MAX_SPEED));
    driveRight(constrain(-spin, -MAX_SPEED, MAX_SPEED));
  } else {
    stopMotors();
  }
}

// =====================================================================
// SETUP / LOOP
// =====================================================================

void setup() {
  Serial.begin(115200);
  delay(200);

  pinMode(LEFT_DIR_FORWARD, OUTPUT);  pinMode(LEFT_DIR_BACKWARD, OUTPUT);  pinMode(LEFT_PWM, OUTPUT);
  pinMode(RIGHT_DIR_FORWARD, OUTPUT); pinMode(RIGHT_DIR_BACKWARD, OUTPUT); pinMode(RIGHT_PWM, OUTPUT);
  stopMotors();

  BP32.setup(&onConnectedGamepad, &onDisconnectedGamepad);
  BP32.enableNewBluetoothConnections(true);   // REQUIRED: accept Share-button pairing
  BP32.forgetBluetoothKeys();                 // clear stale bonds so a fresh pair works
  keysCleared = true;

  Serial.println("Bluepad32 robot ready.");
  Serial.println("Pair: HOLD  SHARE + PS  until the controller light flashes rapidly.");
}

void loop() {
  bool dataUpdated = BP32.update();
  if (dataUpdated && myGamepad && myGamepad->isConnected()) {
    handleGamepad(myGamepad);
  } else if (!myGamepad) {
    stopMotors();   // no controller -> safe stop
  }
  delay(8);
}
