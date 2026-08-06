/*
  esp32_wifi_brain.ino  —  Robot Soccer  (turn-direction cleaned up)
  ==================================================================
  Receives single-byte motor commands from brain_runner.py over UDP and drives
  the robot with IN-PLACE tank turns (matches how the policy trained).

  COMMANDS (single byte over UDP, port 4210)
    'F' Forward   'L' Rotate-LEFT in place (CCW)   'R' Rotate-RIGHT in place (CW)
    'B' Backward  'S' Stop

  ── FIXING TURN DIRECTION — READ THIS ─────────────────────────────────────────
  The turn cases below are STANDARD and must NOT be edited:
      L (left/CCW)  = left wheel backward, right wheel forward
      R (right/CW)  = left wheel forward,  right wheel backward
  If the robot turns the WRONG way (you send 'L' and it rotates right), you have
  ONE knob: flip SWAP_MOTORS below. That swaps which physical motor is treated as
  "left" vs "right" in ONE place. Do NOT also edit the case bodies or the pin
  #defines — editing two things cancels out and leaves you exactly where you
  started (this is the bug that was in the previous version).

  Equivalently you can leave this firmware alone and swap the mapping in
  config.py (ACTION_TO_CMD  2:CMD_RIGHT, 3:CMD_LEFT). Use EXACTLY ONE of the two.

  SETUP
    1. Set WIFI_SSID / WIFI_PASSWORD below.
    2. Flash, open Serial Monitor @115200, copy the printed IP into config.py
       (ROBOT1_IP / ROBOT2_IP).
*/

#include <WiFi.h>
#include <WiFiUdp.h>

// ── CONFIG ───────────────────────────────────────────────────────────────────
const char* WIFI_SSID     = " ";    // Add Your Own WiFi Credentials 
const char* WIFI_PASSWORD = " ";    // Add Your Own WiFi Credentials
const int   UDP_PORT      = 4210;   // commands in  (match config.py ESP32_CMD_PORT)
const int   STATUS_PORT   = 4214;   // status out   (match config.py ESP32_STATUS_PORT)

// ── THE ONE TURN-DIRECTION KNOB ───────────────────────────────────────────────
// false = motor group A is "left", group B is "right".
// true  = swap them. Flip this (and ONLY this) if L/R come out reversed.
#define SWAP_MOTORS false

// ── Motor pins ────────────────────────────────────────────────────────────────
// Group A
#define A_DIR_FORWARD    33
#define A_DIR_BACKWARD   25
#define A_PWM            32
// Group B
#define B_DIR_FORWARD    23
#define B_DIR_BACKWARD   21
#define B_PWM            19

const uint8_t DRIVE_SPEED = 200;   // 0-255
const uint8_t TURN_SPEED  = 180;

#define STATUS_LED 2               // onboard LED

// ── Safety / timing ─────────────────────────────────────────────────────────────
const unsigned long TIMEOUT_MS       = 400;   // stop motors if no command for this long
const unsigned long BEACON_PERIOD_MS = 300;   // how often to send "OK" to the brain

// ── State ────────────────────────────────────────────────────────────────────────
WiFiUDP udp;
char incomingPacket[8];
unsigned long lastCommandTime = 0;
unsigned long lastBeaconTime  = 0;
bool          motorsStopped   = true;
IPAddress     brainIP;
bool          knowBrain = false;

// ── Low-level: drive a specific motor GROUP ───────────────────────────────────
void driveGroupA(int16_t speed) {
  if (speed > 0)      { digitalWrite(A_DIR_FORWARD, HIGH); digitalWrite(A_DIR_BACKWARD, LOW);  analogWrite(A_PWM,  speed); }
  else if (speed < 0) { digitalWrite(A_DIR_FORWARD, LOW);  digitalWrite(A_DIR_BACKWARD, HIGH); analogWrite(A_PWM, -speed); }
  else                { digitalWrite(A_DIR_FORWARD, LOW);  digitalWrite(A_DIR_BACKWARD, LOW);  analogWrite(A_PWM,  0); }
}
void driveGroupB(int16_t speed) {
  if (speed > 0)      { digitalWrite(B_DIR_FORWARD, HIGH); digitalWrite(B_DIR_BACKWARD, LOW);  analogWrite(B_PWM,  speed); }
  else if (speed < 0) { digitalWrite(B_DIR_FORWARD, LOW);  digitalWrite(B_DIR_BACKWARD, HIGH); analogWrite(B_PWM, -speed); }
  else                { digitalWrite(B_DIR_FORWARD, LOW);  digitalWrite(B_DIR_BACKWARD, LOW);  analogWrite(B_PWM,  0); }
}

// ── Logical LEFT / RIGHT wheels (the SWAP_MOTORS knob resolves here, once) ─────
inline void spinLeftMotor(int16_t speed)  { if (SWAP_MOTORS) driveGroupB(speed); else driveGroupA(speed); }
inline void spinRightMotor(int16_t speed) { if (SWAP_MOTORS) driveGroupA(speed); else driveGroupB(speed); }

void stopMotors() { spinLeftMotor(0); spinRightMotor(0); motorsStopped = true; }

// ── Command dispatch — STANDARD tank turns, do not edit ───────────────────────
void executeCommand(char cmd) {
  switch (cmd) {
    case 'F': spinLeftMotor(DRIVE_SPEED);  spinRightMotor(DRIVE_SPEED);  motorsStopped = false; break;
    case 'L': spinLeftMotor(-TURN_SPEED);  spinRightMotor(TURN_SPEED);   motorsStopped = false; break; // CCW
    case 'R': spinLeftMotor(TURN_SPEED);   spinRightMotor(-TURN_SPEED);  motorsStopped = false; break; // CW
    case 'B': spinLeftMotor(-DRIVE_SPEED); spinRightMotor(-DRIVE_SPEED); motorsStopped = false; break;
    case 'S':
    default:  stopMotors(); break;
  }
}

void sendBeacon() {
  if (!knowBrain) return;
  udp.beginPacket(brainIP, STATUS_PORT);
  udp.write((const uint8_t*)"OK", 2);
  udp.endPacket();
}

// ── Setup ──────────────────────────────────────────────────────────────────────
void setup() {
  Serial.begin(115200);
  delay(200);

  pinMode(A_DIR_FORWARD, OUTPUT); pinMode(A_DIR_BACKWARD, OUTPUT); pinMode(A_PWM, OUTPUT);
  pinMode(B_DIR_FORWARD, OUTPUT); pinMode(B_DIR_BACKWARD, OUTPUT); pinMode(B_PWM, OUTPUT);
  pinMode(STATUS_LED, OUTPUT);
  stopMotors();

  Serial.printf("\nConnecting to WiFi: %s\n", WIFI_SSID);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  int attempts = 0;
  while (WiFi.status() != WL_CONNECTED) {
    delay(500); Serial.print("."); digitalWrite(STATUS_LED, attempts % 2);
    if (++attempts > 30) { Serial.println("\nWiFi failed! Check SSID/password and restart."); while (true) delay(1000); }
  }

  Serial.println("\nWiFi connected!");
  Serial.print("ESP32 IP address: "); Serial.println(WiFi.localIP());  // <-- copy into config.py
  Serial.printf("Commands on UDP %d, status beacons to %d, SWAP_MOTORS=%d\n",
                UDP_PORT, STATUS_PORT, SWAP_MOTORS);

  udp.begin(UDP_PORT);
  lastCommandTime = millis();
  lastBeaconTime  = millis();
}

// ── Loop ────────────────────────────────────────────────────────────────────────
void loop() {
  int packetSize = udp.parsePacket();
  if (packetSize > 0) {
    int len = udp.read(incomingPacket, sizeof(incomingPacket) - 1);
    if (len > 0) {
      incomingPacket[len] = '\0';
      brainIP = udp.remoteIP();
      knowBrain = true;
      executeCommand(incomingPacket[0]);
      lastCommandTime = millis();
    }
  }

  // Watchdog — stop if Python goes silent (prevents runaway robot)
  if (millis() - lastCommandTime > TIMEOUT_MS) {
    if (!motorsStopped) { stopMotors(); Serial.println("WATCHDOG: no command — stopped."); }
  }

  // Status beacon — let the brain know we're alive (this is what turns esp32={1:'up'})
  if (millis() - lastBeaconTime > BEACON_PERIOD_MS) {
    sendBeacon();
    lastBeaconTime = millis();
  }

  // LED: solid when actively driven, slow blink when idle/stopped
  bool linkFresh = (millis() - lastCommandTime) < TIMEOUT_MS;
  digitalWrite(STATUS_LED, linkFresh ? HIGH : ((millis() / 500) % 2));

  delay(5);
}
