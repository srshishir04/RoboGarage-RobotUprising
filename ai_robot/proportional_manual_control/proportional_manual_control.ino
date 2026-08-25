/*
  proportional_manual_control.ino  —  Human-Controlled Robot (Bluepad32)
  =======================================================================
  PS4 controller drives an ESP32 4-wheel (2-motor-side) robot over
  Bluetooth, using a single joystick + a "deadman" enable button.

  CONTROLS
    L3 (left stick)   ..... direction robot moves (any angle: fwd, back,
                             fwd-left, back-right, pure turn, etc.)
    R2 (right trigger) ..... ENABLE / deadman switch. Holding R2 alone does
                             NOT move the robot. Releasing R2 always stops
                             the robot immediately, regardless of stick
                             position.
    Stick centered + R2 held ... robot stays still (armed but no command).

  PAIRING (do this every time the light is not already solid)
    1. Power the robot. Open Serial Monitor @115200 -> "Bluepad32 robot ready".
    2. On the PS4 controller, HOLD  SHARE + PS  together until the light
       bar FLASHES rapidly (pairing mode).
    3. Serial prints "Gamepad connected" and the light goes solid. Drive.

  BOARD REQUIREMENT (critical)
    Bluepad32 replaces the ESP32 Bluetooth stack, so you MUST use the
    Bluepad32 board package and select an "ESP32 Bluepad32" board, NOT the
    plain "ESP32 Dev Module".
      • Preferences -> Additional Board Manager URLs, add:
        https://raw.githubusercontent.com/ricardoquesada/esp32-arduino-lib-builder/master/bluepad32_files/package_esp32_bluepad32_index.json
      • Boards Manager -> install "ESP32 Bluepad32 by Ricardo Quesada"
      • Tools -> Board -> pick the Bluepad32 ESP32 Dev Module

  MOTOR WIRING — must match your chassis. If a direction is wrong, use the
  FLIP switches in the CONFIG block below (no need to rewire).
*/

#include <Bluepad32.h>

// =====================================================================
// CONFIG
// =====================================================================

// Motor pins
#define LEFT_DIR_FORWARD    23
#define LEFT_DIR_BACKWARD   21
#define LEFT_PWM            19
#define RIGHT_DIR_FORWARD   33
#define RIGHT_DIR_BACKWARD  25
#define RIGHT_PWM           32

// ---- Direction fix switches (no rewiring needed) --------------------
const bool INVERT_DRIVE = false;   // flip forward/backward globally
const bool SWAP_STEER   = false;   // flip left/right steering
const bool INVERT_LEFT  = false;   // flip left motor only
const bool INVERT_RIGHT = false;   // flip right motor only

const uint8_t MAX_SPEED        = 255;   // 0..255 PWM ceiling
const uint8_t MIN_DRIVE_SPEED  = 45;    // overcome motor stall (raise if it stalls)
const int     STICK_DEADZONE   = 40;    // |axis| below this = no input (0..512 scale)
const int     ENABLE_THRESHOLD = 100;   // R2 analog value (0..1023) counted as "held"

ControllerPtr myGamepad = nullptr;

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
    gp->setColorLED(0, 255, 0);   // green = connected (PS4 light bar)
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

int16_t scaleAxis(int raw) {
  int trimmed = (raw > 0) ? raw - STICK_DEADZONE : raw + STICK_DEADZONE;
  return (int16_t)map(trimmed, -(512 - STICK_DEADZONE), (512 - STICK_DEADZONE),
                       -MAX_SPEED, MAX_SPEED);
}

void handleGamepad(ControllerPtr ctl) {
  if (!ctl || !ctl->isConnected()) { stopMotors(); return; }

  // ---- R2 deadman / enable switch -----------------------------------
  bool enabled = ctl->r2() > ENABLE_THRESHOLD;

  if (!enabled) {
    stopMotors();
    return;
  }

  // ---- L3 stick: read both axes --------------------------------------
  int rawX = ctl->axisX();   // -512..511, + = right
  int rawY = ctl->axisY();   // -512..511, + = down on most Bluepad32 pads

  bool xActive = abs(rawX) > STICK_DEADZONE;
  bool yActive = abs(rawY) > STICK_DEADZONE;

  if (!xActive && !yActive) {
    stopMotors();
    return;
  }

  // Forward = pushing stick UP, which Bluepad32 reports as negative Y.
  int16_t base  = yActive ? (int16_t)(-scaleAxis(rawY)) : 0;
  int16_t steer = xActive ? scaleAxis(rawX)              : 0;

  if (base == 0 && steer != 0) {
    int16_t spin = steer / 2;
    driveLeft(constrain(spin, -MAX_SPEED, MAX_SPEED));
    driveRight(constrain(-spin, -MAX_SPEED, MAX_SPEED));
    return;
  }

  drive(base, steer);
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
  BP32.enableNewBluetoothConnections(true);   // REQUIRED: accept SHARE+PS pairing

  Serial.println("Bluepad32 robot ready.");
  Serial.println("Pair: HOLD  SHARE + PS  on the PS4 controller until the light flashes rapidly.");
  Serial.println("Hold R2 to enable, then move L3 to drive.");
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