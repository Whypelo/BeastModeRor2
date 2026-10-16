using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using RoR2;
using UnityEngine;
using HarmonyLib;
using System.Collections.Generic;

namespace BeastModeRor2
{
    // Класс настроек камеры для одного персонажа
    public class CameraSettings
    {
        public float upOffset;
        public float forwardOffset;
        
        public CameraSettings(float up, float forward)
        {
            upOffset = up;
            forwardOffset = forward;
        }
    }

    [BepInPlugin("com.melty.beastmoderor2", "Beast Mode", "0.1.0")]
    [BepInProcess("Risk of Rain 2.exe")]
    public class BeastModePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        public const int OVERKILL_THRESHOLD = 3;
        public const float OVERKILL_DAMAGE_MULT = 1.5f;
        public const float OVERKILL_SPEED_MULT = 1.2f;
        public const float SLIDE_SPEED = 20f;
        public const float SLIDE_DURATION = 0.4f;

        // Настройки FOV
        internal static ConfigEntry<float> ConfigFOV;
        public const float FOV_MIN = 30f;
        public const float FOV_MAX = 120f;
        public const float FOV_STEP = 5f;
        public const float FOV_DEFAULT = 80f;

        // Настройки камеры
        public const float CAM_STEP = 0.05f;           // Обычный шаг
        public const float CAM_STEP_FINE = 0.01f;      // Точный шаг (с Shift)
        public const float CAM_UP_DEFAULT = 0.05f;
        public const float CAM_FORWARD_DEFAULT = 0.15f;
        public const float CAM_UP_MIN = -1f;
        public const float CAM_UP_MAX = 2f;
        public const float CAM_FORWARD_MIN = -0.5f;
        public const float CAM_FORWARD_MAX = 1f;

        // Словарь настроек камеры для каждого персонажа (ключ - имя body)
        internal static Dictionary<string, CameraSettings> characterCameraSettings = new Dictionary<string, CameraSettings>();
        
        // Хранилище конфигов для каждого персонажа (чтобы BepInEx сохранял их)
        private static Dictionary<string, ConfigEntry<float>> configUpEntries = new Dictionary<string, ConfigEntry<float>>();
        private static Dictionary<string, ConfigEntry<float>> configForwardEntries = new Dictionary<string, ConfigEntry<float>>();

        internal int killsWithoutDamage = 0;
        internal bool isOverkillActive = false;
        private float slideTimer = 0f;
        private bool isSliding = false;
        internal CharacterBody playerBody;
        internal string currentCharacterName = "";

        public void Awake()
        {
            Log = Logger;
            Log.LogInfo("[BeastMode] Initializing...");
            
            ConfigFOV = Config.Bind<float>(
                "Camera", 
                "FieldOfView", 
                FOV_DEFAULT,
                new ConfigDescription(
                    "Поле зрения камеры (FOV). Меняется в игре клавишами [ и ].",
                    new AcceptableValueRange<float>(FOV_MIN, FOV_MAX)
                )
            );
            
            var harmony = new Harmony("com.melty.beastmoderor2");
            harmony.PatchAll();

            Log.LogInfo($"[BeastMode] Beast Mode loaded. FOV: {ConfigFOV.Value}");
        }

        // === ПОЛУЧЕНИЕ ИЛИ СОЗДАНИЕ НАСТРОЕК ДЛЯ ПЕРСОНАЖА ===
        internal static CameraSettings GetOrCreateSettings(string characterName, ConfigFile configFile)
        {
            if (characterCameraSettings.TryGetValue(characterName, out CameraSettings existing))
            {
                return existing;
            }

            // Создаём новые настройки
            CameraSettings settings = new CameraSettings(CAM_UP_DEFAULT, CAM_FORWARD_DEFAULT);

            // Пытаемся загрузить из конфига
            string safeName = SanitizeConfigKey(characterName);
            
            if (!configUpEntries.TryGetValue(safeName, out ConfigEntry<float> upEntry))
            {
                upEntry = configFile.Bind<float>(
                    $"Character.{safeName}",
                    "UpOffset",
                    CAM_UP_DEFAULT,
                    new ConfigDescription(
                        $"Смещение камеры вверх для персонажа {characterName}. Стрелки ↑↓ в игре.",
                        new AcceptableValueRange<float>(CAM_UP_MIN, CAM_UP_MAX)
                    )
                );
                configUpEntries[safeName] = upEntry;
            }

            if (!configForwardEntries.TryGetValue(safeName, out ConfigEntry<float> forwardEntry))
            {
                forwardEntry = configFile.Bind<float>(
                    $"Character.{safeName}",
                    "ForwardOffset",
                    CAM_FORWARD_DEFAULT,
                    new ConfigDescription(
                        $"Смещение камеры вперёд для персонажа {characterName}. Стрелки ←→ в игре.",
                        new AcceptableValueRange<float>(CAM_FORWARD_MIN, CAM_FORWARD_MAX)
                    )
                );
                configForwardEntries[safeName] = forwardEntry;
            }

            settings.upOffset = upEntry.Value;
            settings.forwardOffset = forwardEntry.Value;

            characterCameraSettings[characterName] = settings;
            Log.LogInfo($"[BeastMode] Created camera settings for {characterName}: Up={settings.upOffset}, Forward={settings.forwardOffset}");
            
            return settings;
        }

        // Очистка имени для использования в конфиге
        private static string SanitizeConfigKey(string name)
        {
            return name.Replace(" ", "_").Replace(".", "_").Replace("/", "_");
        }

        // === ПОЛУЧЕНИЕ ТЕКУЩИХ НАСТРОЕК ===
        internal static CameraSettings GetCurrentSettings()
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || string.IsNullOrEmpty(plugin.currentCharacterName))
            {
                return new CameraSettings(CAM_UP_DEFAULT, CAM_FORWARD_DEFAULT);
            }
            
            if (characterCameraSettings.TryGetValue(plugin.currentCharacterName, out CameraSettings settings))
            {
                return settings;
            }
            return new CameraSettings(CAM_UP_DEFAULT, CAM_FORWARD_DEFAULT);
        }

        // === СОХРАНЕНИЕ НАСТРОЕК В КОНФИГ ===
        internal static void SaveCurrentSettings()
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || string.IsNullOrEmpty(plugin.currentCharacterName)) return;
            
            if (!characterCameraSettings.TryGetValue(plugin.currentCharacterName, out CameraSettings settings)) return;
            
            string safeName = SanitizeConfigKey(plugin.currentCharacterName);
            
            if (configUpEntries.TryGetValue(safeName, out ConfigEntry<float> upEntry))
            {
                upEntry.Value = settings.upOffset;
            }
            if (configForwardEntries.TryGetValue(safeName, out ConfigEntry<float> forwardEntry))
            {
                forwardEntry.Value = settings.forwardOffset;
            }
        }

        private void SendChatMessage(string message)
        {
            Chat.AddMessage($"<color=#FFD700>[BeastMode]</color> {message}");
        }

        internal void ActivateOverkill()
        {
            if (playerBody == null) return;
            isOverkillActive = true;
            
            playerBody.baseDamage *= OVERKILL_DAMAGE_MULT;
            playerBody.baseMoveSpeed *= OVERKILL_SPEED_MULT;
            
            playerBody.RecalculateStats();
            
            SendChatMessage("<color=#FF4444><b>OVERKILL ACTIVATED!</b></color>");
        }

        internal void DeactivateOverkill()
        {
            if (!isOverkillActive || playerBody == null) return;
            isOverkillActive = false;
            
            playerBody.baseDamage /= OVERKILL_DAMAGE_MULT;
            playerBody.baseMoveSpeed /= OVERKILL_SPEED_MULT;
            
            playerBody.RecalculateStats();
            killsWithoutDamage = 0;
            
            SendChatMessage("<color=#888888>Overkill deactivated.</color>");
        }

        public void Update()
        {
            // === УПРАВЛЕНИЕ FOV ===
            if (Input.GetKeyDown(KeyCode.LeftBracket))
            {
                ConfigFOV.Value = Mathf.Clamp(ConfigFOV.Value - FOV_STEP, FOV_MIN, FOV_MAX);
                SendChatMessage($"FOV: <b>{ConfigFOV.Value:F0}</b>");
            }
            
            if (Input.GetKeyDown(KeyCode.RightBracket))
            {
                ConfigFOV.Value = Mathf.Clamp(ConfigFOV.Value + FOV_STEP, FOV_MIN, FOV_MAX);
                SendChatMessage($"FOV: <b>{ConfigFOV.Value:F0}</b>");
            }
            
            if (Input.GetKeyDown(KeyCode.Backspace))
            {
                ConfigFOV.Value = FOV_DEFAULT;
                SendChatMessage($"FOV reset to <b>{FOV_DEFAULT:F0}</b>");
            }

            // === УПРАВЛЕНИЕ КАМЕРОЙ СТРЕЛКАМИ ===
            if (!string.IsNullOrEmpty(currentCharacterName) && 
                characterCameraSettings.TryGetValue(currentCharacterName, out CameraSettings settings))
            {
                float step = Input.GetKey(KeyCode.LeftShift) ? CAM_STEP_FINE : CAM_STEP;
                bool changed = false;

                // ↑ - увеличить высоту
                if (Input.GetKeyDown(KeyCode.UpArrow))
                {
                    settings.upOffset = Mathf.Clamp(settings.upOffset + step, CAM_UP_MIN, CAM_UP_MAX);
                    changed = true;
                }
                
                // ↓ - уменьшить высоту
                if (Input.GetKeyDown(KeyCode.DownArrow))
                {
                    settings.upOffset = Mathf.Clamp(settings.upOffset - step, CAM_UP_MIN, CAM_UP_MAX);
                    changed = true;
                }
                
                // → - увеличить смещение вперёд
                if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    settings.forwardOffset = Mathf.Clamp(settings.forwardOffset + step, CAM_FORWARD_MIN, CAM_FORWARD_MAX);
                    changed = true;
                }
                
                // ← - уменьшить смещение вперёд
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    settings.forwardOffset = Mathf.Clamp(settings.forwardOffset - step, CAM_FORWARD_MIN, CAM_FORWARD_MAX);
                    changed = true;
                }

                // Сброс настроек камеры для текущего персонажа (Delete)
                if (Input.GetKeyDown(KeyCode.Delete))
                {
                    settings.upOffset = CAM_UP_DEFAULT;
                    settings.forwardOffset = CAM_FORWARD_DEFAULT;
                    changed = true;
                    SendChatMessage($"<color=#AAAAAA>{currentCharacterName}</color> camera reset");
                }

                if (changed)
                {
                    SendChatMessage(
                        $"<color=#AAAAAA>{currentCharacterName}</color>  " +
                        $"Up: <b>{settings.upOffset:F2}</b>  |  " +
                        $"Fwd: <b>{settings.forwardOffset:F2}</b>" +
                        (Input.GetKey(KeyCode.LeftShift) ? "  <color=#88FFFF>[fine]</color>" : "")
                    );
                    SaveCurrentSettings();
                }
            }

            // === СЛАЙД ===
            if (playerBody == null || !playerBody.isPlayerControlled) return;

            if (Input.GetKeyDown(KeyCode.LeftControl) && !isSliding)
            {
                StartSlide();
            }

            if (isSliding)
            {
                slideTimer -= Time.deltaTime;
                if (slideTimer <= 0f)
                {
                    isSliding = false;
                }
            }
        }

        private void StartSlide()
        {
            isSliding = true;
            slideTimer = SLIDE_DURATION;

            var motor = playerBody.characterMotor;
            if (motor != null)
            {
                Vector3 slideDir = playerBody.inputBank.moveVector;
                if (slideDir.sqrMagnitude < 0.01f) slideDir = playerBody.gameObject.transform.forward;
                slideDir.y = 0f;
                slideDir.Normalize();

                motor.velocity += slideDir * SLIDE_SPEED;
                Log.LogInfo("[BeastMode] Slide triggered!");
            }
        }
    }

    [HarmonyPatch(typeof(CharacterBody), "Start")]
    public static class CharacterBodyStartPatch
    {
        static void Postfix(CharacterBody __instance)
        {
            if (__instance.isPlayerControlled)
            {
                var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
                if (plugin != null)
                {
                    plugin.playerBody = __instance;
                    
                    // Получаем имя персонажа и создаём/загружаем настройки для него
                    string characterName = __instance.baseNameToken;
                    if (string.IsNullOrEmpty(characterName))
                    {
                        characterName = __instance.name;
                    }
                    
                    plugin.currentCharacterName = characterName;
                    BeastModePlugin.GetOrCreateSettings(characterName, plugin.Config);
                    
                    BeastModePlugin.Log.LogInfo($"[BeastMode] Player body attached: {characterName}");
                    Chat.AddMessage($"<color=#FFD700>[BeastMode]</color> Playing as <color=#AAAAAA>{characterName}</color>");
                }
            }
        }
    }

    public static class LastAttackerTracker
    {
        public static Dictionary<HealthComponent, GameObject> LastAttackers = new Dictionary<HealthComponent, GameObject>();
    }

    [HarmonyPatch(typeof(HealthComponent), "TakeDamage")]
    public static class HealthComponentTakeDamagePatch
    {
        static void Postfix(HealthComponent __instance, DamageInfo damageInfo)
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || plugin.playerBody == null) return;
            
            if (damageInfo.attacker != null)
            {
                LastAttackerTracker.LastAttackers[__instance] = damageInfo.attacker;
            }
            
            if (__instance.body != plugin.playerBody) return;
            if (damageInfo.damage <= 0 || damageInfo.attacker == __instance.gameObject) return;

            plugin.killsWithoutDamage = 0;
            
            if (plugin.isOverkillActive)
            {
                plugin.DeactivateOverkill();
            }
        }
    }

    [HarmonyPatch(typeof(HealthComponent), "Die")]
    public static class HealthComponentDiePatch
    {
        static void Postfix(HealthComponent __instance, bool noCorpse)
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || plugin.playerBody == null) return;
            
            if (__instance.body != null && __instance.body.isPlayerControlled) return;
            
            if (LastAttackerTracker.LastAttackers.TryGetValue(__instance, out GameObject attacker))
            {
                if (attacker == plugin.playerBody.gameObject)
                {
                    plugin.killsWithoutDamage++;
                    
                    if (plugin.killsWithoutDamage >= BeastModePlugin.OVERKILL_THRESHOLD && !plugin.isOverkillActive)
                    {
                        plugin.ActivateOverkill();
                    }
                }
                
                LastAttackerTracker.LastAttackers.Remove(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(CameraRigController), "LateUpdate")]
    public static class FirstPersonCameraPatch
    {
        private static bool headRenderersDisabled = false;
        private static CharacterBody lastProcessedBody = null;

        static void Postfix(CameraRigController __instance)
        {
            if (__instance == null) return;
            
            var targetObj = __instance.target;
            if (targetObj == null) return;
            
            var body = targetObj.GetComponent<CharacterBody>();
            if (body == null || !body.isPlayerControlled) return;

            Transform model = body.modelLocator?.modelTransform;
            if (model == null) return;

            Transform head = FindHeadBone(model);
            
            var cam = Camera.main;
            if (cam == null) return;

            // Получаем настройки для текущего персонажа
            CameraSettings settings = BeastModePlugin.GetCurrentSettings();

            Vector3 headPos;
            
            if (head != null)
            {
                headPos = head.position 
                        + head.up * settings.upOffset
                        + cam.transform.forward * settings.forwardOffset;
                
                if (!headRenderersDisabled || lastProcessedBody != body)
                {
                    DisableHeadRenderers(head);
                    headRenderersDisabled = true;
                    lastProcessedBody = body;
                }
            }
            else
            {
                Transform core = body.coreTransform;
                headPos = core != null 
                    ? core.position + Vector3.up * 1.7f 
                    : body.transform.position + Vector3.up * 1.7f;
            }
            
            cam.transform.position = headPos;
            cam.fieldOfView = BeastModePlugin.ConfigFOV.Value;
        }

        private static Transform FindHeadBone(Transform model)
        {
            string[] headNames = new string[]
            {
                "Head", "head",
                "Bip001 Head", "Bip001 Head1",
                "Neck", "neck",
                "Bip001 Neck"
            };

            foreach (string name in headNames)
            {
                Transform found = FindChildRecursive(model, name);
                if (found != null) return found;
            }
            return null;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                Transform found = FindChildRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static void DisableHeadRenderers(Transform head)
        {
            if (head == null) return;
            
            Renderer[] renderers = head.GetComponentsInChildren<Renderer>();
            foreach (Renderer renderer in renderers)
            {
                renderer.enabled = false;
            }
        }
    }
}