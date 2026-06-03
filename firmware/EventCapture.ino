// =============================================================
//  EventCapture — ESP32 Remote Controller
// =============================================================
//  Схема підключення:
//    D18 (GPIO18) — кнопка "Скріншот"  → GND
//    D19 (GPIO19) — кнопка "Відео"     → GND
//    D2  (GPIO2)  — вбудований LED (індикатор активності)
//    D4  (GPIO4)  — зовнішній LED скріншот (через 330 Ом → GND)
//    D5  (GPIO5)  — зовнішній LED відео   (через 330 Ом → GND)
// =============================================================

// ── Піни ──────────────────────────────────────────────────────
#define BTN_SCREENSHOT   18
#define BTN_VIDEO        19
#define LED_BUILTIN_PIN   2   // вбудований синій LED
#define LED_SCREENSHOT    4   // зовнішній LED — скріншот
#define LED_VIDEO_PIN     5   // зовнішній LED — відео

// ── Серійний порт ─────────────────────────────────────────────
#define BAUD_RATE        115200

// ── Дебаунс кнопок ────────────────────────────────────────────
#define DEBOUNCE_MS      50

// ── Хендшейк: надсилати кожні N мс ───────────────────────────
#define HANDSHAKE_INTERVAL_MS  10000

// =============================================================
//  Структура дебаунсу кнопки
// =============================================================
struct Button {
    uint8_t  pin;
    bool     lastRaw;
    bool     confirmed;
    uint32_t lastChangeMs;
};

Button btnScreenshot = { BTN_SCREENSHOT, HIGH, HIGH, 0 };
Button btnVideo      = { BTN_VIDEO,      HIGH, HIGH, 0 };

// =============================================================
//  Вбудований LED — неблокуючий автомат станів
// =============================================================
enum LedMode { LED_HEARTBEAT, LED_FLASH };

LedMode  ledMode       = LED_HEARTBEAT;
uint32_t ledTimer      = 0;
int      ledStep       = 0;
int      ledFlashCount = 0;

#define HEARTBEAT_ON   150
#define HEARTBEAT_OFF  2000
#define FLASH_ON       120
#define FLASH_OFF      130
#define FLASH_PAUSE    500

// ── Хендшейк ──────────────────────────────────────────────────
uint32_t lastHandshakeMs = 0;

// =============================================================
//  SETUP
// =============================================================
void setup() {
    Serial.begin(BAUD_RATE);

    pinMode(LED_BUILTIN_PIN, OUTPUT);
    pinMode(LED_SCREENSHOT,  OUTPUT);
    pinMode(LED_VIDEO_PIN,   OUTPUT);
    pinMode(BTN_SCREENSHOT,  INPUT_PULLUP);
    pinMode(BTN_VIDEO,       INPUT_PULLUP);

    // Всі LED вимкнути
    digitalWrite(LED_SCREENSHOT, LOW);
    digitalWrite(LED_VIDEO_PIN,  LOW);

    // Анімація старту (вбудований LED)
    for (int i = 0; i < 3; i++) {
        digitalWrite(LED_BUILTIN_PIN, HIGH); delay(80);
        digitalWrite(LED_BUILTIN_PIN, LOW);  delay(80);
    }

    // Перший хендшейк
    Serial.println("EVENTCAPTURE_DEVICE");
    lastHandshakeMs = millis();

    ledMode  = LED_HEARTBEAT;
    ledStep  = 1;
    ledTimer = millis();
}

// =============================================================
//  LOOP
// =============================================================
void loop() {
    handleButton(btnScreenshot, "SCREENSHOT", 1, LED_SCREENSHOT);
    handleButton(btnVideo,      "SAVE_VIDEO", 2, LED_VIDEO_PIN);
    updateBuiltinLed();
    sendHandshakePeriodically();
}

// =============================================================
//  Хендшейк кожні HANDSHAKE_INTERVAL_MS мс
// =============================================================
void sendHandshakePeriodically() {
    if (millis() - lastHandshakeMs >= HANDSHAKE_INTERVAL_MS) {
        Serial.println("EVENTCAPTURE_DEVICE");
        lastHandshakeMs = millis();
    }
}

// =============================================================
//  Обробка кнопки з дебаунсом
//  externalLedPin: зовнішній LED що горить поки кнопка натиснута
// =============================================================
void handleButton(Button &btn, const char* command,
                  int flashCount, uint8_t externalLedPin) {
    bool raw = digitalRead(btn.pin);

    if (raw != btn.lastRaw) {
        btn.lastChangeMs = millis();
        btn.lastRaw = raw;
    }

    if ((millis() - btn.lastChangeMs) > DEBOUNCE_MS) {
        if (raw != btn.confirmed) {
            btn.confirmed = raw;

            if (btn.confirmed == LOW) {
                // Кнопку натиснуто
                Serial.println(command);
                digitalWrite(externalLedPin, HIGH);  // LED ввімкнути
                triggerFlash(flashCount);             // вбудований LED
            } else {
                // Кнопку відпущено
                digitalWrite(externalLedPin, LOW);   // LED вимкнути
            }
        }
    }
}

// =============================================================
//  Запустити N спалахів вбудованого LED
// =============================================================
void triggerFlash(int count) {
    ledMode       = LED_FLASH;
    ledFlashCount = count * 2 - 1;
    ledStep       = 0;
    ledTimer      = millis();
    digitalWrite(LED_BUILTIN_PIN, HIGH);
}

// =============================================================
//  Автомат стану вбудованого LED (неблокуючий)
// =============================================================
void updateBuiltinLed() {
    uint32_t now = millis();

    switch (ledMode) {

        case LED_HEARTBEAT:
            if (ledStep == 0 && now - ledTimer >= HEARTBEAT_ON) {
                digitalWrite(LED_BUILTIN_PIN, LOW);
                ledStep  = 1;
                ledTimer = now;
            } else if (ledStep == 1 && now - ledTimer >= HEARTBEAT_OFF) {
                digitalWrite(LED_BUILTIN_PIN, HIGH);
                ledStep  = 0;
                ledTimer = now;
            }
            break;

        case LED_FLASH:
            if (ledFlashCount > 0) {
                uint32_t dur = (ledStep % 2 == 0) ? FLASH_ON : FLASH_OFF;
                if (now - ledTimer >= dur) {
                    ledFlashCount--;
                    ledStep++;
                    ledTimer = now;
                    digitalWrite(LED_BUILTIN_PIN,
                                 (ledStep % 2 == 0) ? HIGH : LOW);
                }
            } else {
                if (now - ledTimer >= FLASH_PAUSE) {
                    ledMode  = LED_HEARTBEAT;
                    ledStep  = 1;
                    ledTimer = now;
                    digitalWrite(LED_BUILTIN_PIN, LOW);
                }
            }
            break;
    }
}
