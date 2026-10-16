using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using RoR2;
using UnityEngine;
using HarmonyLib;
using System.Collections.Generic;

namespace BeastModeRor2
{
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
        public const float SLIDE_SPEED = 20f;
        public const float SLIDE_DURATION = 0.4f;

        internal static ConfigEntry<float> ConfigFOV;
        public const float FOV_MIN = 30f;
        public const float FOV_MAX = 120f;
        public const float FOV_STEP = 5f;
        public const float FOV_DEFAULT = 80f;

        public const float CAM_STEP = 0.05f;
        public const float CAM_STEP_FINE = 0.01f;
        public const float CAM_UP_MIN = -1f;
        public const float CAM_UP_MAX = 2f;
        public const float CAM_FORWARD_MIN = -0.5f;
        public const float CAM_FORWARD_MAX = 1f;

        internal static readonly Dictionary<string, CameraSettings> DefaultCharacterSettings = 
            new Dictionary<string, CameraSettings>
        {
            { "BANDIT2_BODY_NAME",   new CameraSettings(0.25f,  0.35f) },
            { "COMMANDO_BODY_NAME",  new CameraSettings(0.30f,  0.15f) },
            { "CROCO_BODY_NAME",     new CameraSettings(0.55f,  0.70f) },
            { "ENGI_BODY_NAME",      new CameraSettings(0.00f,  0.10f) },
            { "HUNTRESS_BODY_NAME",  new CameraSettings(0.05f,  0.25f) },
            { "LOADER_BODY_NAME",    new CameraSettings(0.05f,  0.15f) },
            { "MAGE_BODY_NAME",      new CameraSettings(0.25f,  0.10f) },
            { "MERC_BODY_NAME",      new CameraSettings(0.80f,  0.85f) },
            { "SEEKER_BODY_NAME",    new CameraSettings(0.05f,  0.15f) },
            { "TOOLBOT_BODY_NAME",   new CameraSettings(1.20f, -0.30f) },
        };

        public const float CAM_UP_DEFAULT = 0.05f;
        public const float CAM_FORWARD_DEFAULT = 0.15f;

        internal static Dictionary<string, CameraSettings> characterCameraSettings = new Dictionary<string, CameraSettings>();
        private static Dictionary<string, ConfigEntry<float>> configUpEntries = new Dictionary<string, ConfigEntry<float>>();
        private static Dictionary<string, ConfigEntry<float>> configForwardEntries = new Dictionary<string, ConfigEntry<float>>();

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

        internal static CameraSettings GetOrCreateSettings(string characterName, ConfigFile configFile)
        {
            if (characterCameraSettings.TryGetValue(characterName, out CameraSettings existing))
            {
                return existing;
            }

            CameraSettings defaults;
            if (!DefaultCharacterSettings.TryGetValue(characterName, out defaults))
            {
                defaults = new CameraSettings(CAM_UP_DEFAULT, CAM_FORWARD_DEFAULT);
            }

            CameraSettings settings = new CameraSettings(defaults.upOffset, defaults.forwardOffset);

            string safeName = SanitizeConfigKey(characterName);
            
            if (!configUpEntries.TryGetValue(safeName, out ConfigEntry<float> upEntry))
            {
                upEntry = configFile.Bind<float>(
                    $"Character.{safeName}",
                    "UpOffset",
                    defaults.upOffset,
                    new ConfigDescription(
                        $"Смещение камеры вверх для {characterName}. Стрелки ↑↓ в игре.",
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
                    defaults.forwardOffset,
                    new ConfigDescription(
                        $"Смещение камеры вперёд для {characterName}. Стрелки ←→ в игре.",
                        new AcceptableValueRange<float>(CAM_FORWARD_MIN, CAM_FORWARD_MAX)
                    )
                );
                configForwardEntries[safeName] = forwardEntry;
            }

            settings.upOffset = upEntry.Value;
            settings.forwardOffset = forwardEntry.Value;

            characterCameraSettings[characterName] = settings;
            Log.LogInfo($"[BeastMode] Loaded settings for {characterName}: Up={settings.upOffset:F3}, Forward={settings.forwardOffset:F3}");
            
            return settings;
        }

        private static string SanitizeConfigKey(string name)
        {
            return name.Replace(" ", "_").Replace(".", "_").Replace("/", "_");
        }

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

        internal void SendChatMessage(string message)
        {
            Chat.AddMessage($"<color=#FFD700>[BeastMode]</color> {message}");
        }

        public void Update()
        {
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

            if (!string.IsNullOrEmpty(currentCharacterName) && 
                characterCameraSettings.TryGetValue(currentCharacterName, out CameraSettings settings))
            {
                float step = Input.GetKey(KeyCode.LeftShift) ? CAM_STEP_FINE : CAM_STEP;
                bool changed = false;

                if (Input.GetKeyDown(KeyCode.UpArrow))
                {
                    settings.upOffset = Mathf.Clamp(settings.upOffset + step, CAM_UP_MIN, CAM_UP_MAX);
                    changed = true;
                }
                
                if (Input.GetKeyDown(KeyCode.DownArrow))
                {
                    settings.upOffset = Mathf.Clamp(settings.upOffset - step, CAM_UP_MIN, CAM_UP_MAX);
                    changed = true;
                }
                
                if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    settings.forwardOffset = Mathf.Clamp(settings.forwardOffset + step, CAM_FORWARD_MIN, CAM_FORWARD_MAX);
                    changed = true;
                }
                
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    settings.forwardOffset = Mathf.Clamp(settings.forwardOffset - step, CAM_FORWARD_MIN, CAM_FORWARD_MAX);
                    changed = true;
                }

                if (Input.GetKeyDown(KeyCode.Delete))
                {
                    if (DefaultCharacterSettings.TryGetValue(currentCharacterName, out CameraSettings defaults))
                    {
                        settings.upOffset = defaults.upOffset;
                        settings.forwardOffset = defaults.forwardOffset;
                    }
                    else
                    {
                        settings.upOffset = CAM_UP_DEFAULT;
                        settings.forwardOffset = CAM_FORWARD_DEFAULT;
                    }
                    changed = true;
                    SendChatMessage($"<color=#AAAAAA>{currentCharacterName}</color> camera reset to defaults");
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

            if (playerBody == null || !playerBody.isPlayerControlled) return;

            if (Input.GetKeyDown(KeyCode.LeftAlt) && !isSliding)
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
                SendChatMessage("<color=#88FFFF>⚡ Slide!</color>");
                
                PlaySlideAnimation();
            }
        }

        // === ВСПОМОГАТЕЛЬНЫЙ МЕТОД ДЛЯ СОВМЕСТИМОСТИ С .NET FRAMEWORK 4.7.2 ===
        private static bool ContainsIgnoreCase(string source, string value)
        {
            if (source == null || value == null) return false;
            return source.IndexOf(value, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

                // === ВОСПРОИЗВЕДЕНИЕ АНИМАЦИИ СЛАЙДА ===
        private void PlaySlideAnimation()
        {
            if (playerBody == null) return;
            
            // === ВАЖНО: В RoR2 используем CharacterModel.mainAnimator ===
            // === ПОИСК ANIMATOR НАПРЯМУЮ НА МОДЕЛИ ===
            Transform modelTransform = playerBody.modelLocator?.modelTransform;
            if (modelTransform == null)
            {
                Log.LogWarning($"[BeastMode] No modelTransform found for {currentCharacterName}");
                return;
            }
            
            Animator mainAnimator = modelTransform.GetComponent<Animator>();
            if (mainAnimator == null)
            {
                mainAnimator = modelTransform.GetComponentInChildren<Animator>();
            }
            
            if (mainAnimator == null)
            {
                Log.LogWarning($"[BeastMode] No Animator found for {currentCharacterName}");
                return;
            }
            
            Log.LogInfo($"[BeastMode] Found Animator on {mainAnimator.gameObject.name} for {currentCharacterName}");
            
            if (mainAnimator == null)
            {
                Log.LogWarning($"[BeastMode] No Animator found for {currentCharacterName}");
                return;
            }

            LogAllAnimations(mainAnimator);

            // === СПИСОК ИМЕН АНИМАЦИИ СЛАЙДА ===
            string[] slideAnimNames = new string[]
            {
                "SlideForward",
                "TacticalDive",
                "TacticalSlide",
                "Slide",
                "Dive",
                "CombatDive",
                "Roll"
            };

            // === ПОПЫТКА 1: CrossFade (самый надёжный способ в RoR2) ===
            foreach (string animName in slideAnimNames)
            {
                if (TryCrossFadeAnimation(mainAnimator, animName))
                {
                    Log.LogInfo($"[BeastMode] Successfully cross-faded slide animation '{animName}' for {currentCharacterName}");
                    return;
                }
            }

            // === ПОПЫТКА 2: Поиск по частичному совпадению + CrossFade ===
            if (mainAnimator.runtimeAnimatorController != null)
            {
                foreach (AnimationClip clip in mainAnimator.runtimeAnimatorController.animationClips)
                {
                    if (ContainsIgnoreCase(clip.name, "Slide") ||
                        ContainsIgnoreCase(clip.name, "Dive") ||
                        ContainsIgnoreCase(clip.name, "Roll"))
                    {
                        // Используем ТОЧНОЕ имя клипа для CrossFade
                        mainAnimator.CrossFade(clip.name, 0.05f, 0, 0f);
                        mainAnimator.Update(0f); // Принудительное обновление
                        Log.LogInfo($"[BeastMode] Cross-faded partial match animation '{clip.name}' for {currentCharacterName}");
                        return;
                    }
                }
            }

            // === ПОПЫТКА 3: Прямое воспроизведение через Play с обновлением ===
            foreach (string animName in slideAnimNames)
            {
                if (TryPlayAnimationWithUpdate(mainAnimator, animName))
                {
                    Log.LogInfo($"[BeastMode] Played with update '{animName}' for {currentCharacterName}");
                    return;
                }
            }

            // === ПОПЫТКА 4: Fallback анимации ===
            string[] fallbackAnims = new string[]
            {
                "Dodge", "Dash", "Blink", "Evade", "Jump", "Fall"
            };

            foreach (string animName in fallbackAnims)
            {
                if (TryCrossFadeAnimation(mainAnimator, animName))
                {
                    Log.LogInfo($"[BeastMode] Cross-faded fallback animation '{animName}' for {currentCharacterName}");
                    return;
                }
            }

            // === ПОПЫТКА 5: Триггеры ===
            string[] triggers = new string[] { "Slide", "SlideForward", "TacticalDive", "Dodge", "Dash" };
            foreach (string triggerName in triggers)
            {
                if (TrySetTrigger(mainAnimator, triggerName))
                {
                    Log.LogInfo($"[BeastMode] Triggered '{triggerName}' for {currentCharacterName}");
                    return;
                }
            }

            Log.LogWarning($"[BeastMode] No suitable slide animation found for {currentCharacterName}. Check Log.txt for available animations.");
        }

        // === НОВЫЙ МЕТОД: CrossFade (правильный способ для RoR2) ===
        private bool TryCrossFadeAnimation(Animator animator, string animName)
        {
            if (animator.runtimeAnimatorController == null) return false;
            
            foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
            {
                if (clip.name.Equals(animName, System.StringComparison.OrdinalIgnoreCase) ||
                    clip.name.EndsWith("|" + animName, System.StringComparison.OrdinalIgnoreCase) ||
                    ContainsIgnoreCase(clip.name, animName))
                {
                    // CrossFade с быстрым переходом (0.05 сек)
                    animator.CrossFade(clip.name, 0.05f, 0, 0f);
                    // Принудительное обновление animator для немедленного эффекта
                    animator.Update(0f);
                    return true;
                }
            }
            return false;
        }

        // === НОВЫЙ МЕТОД: Play с принудительным обновлением ===
        private bool TryPlayAnimationWithUpdate(Animator animator, string animName)
        {
            if (animator.runtimeAnimatorController == null) return false;
            
            foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
            {
                if (clip.name.Equals(animName, System.StringComparison.OrdinalIgnoreCase) ||
                    clip.name.EndsWith("|" + animName, System.StringComparison.OrdinalIgnoreCase) ||
                    ContainsIgnoreCase(clip.name, animName))
                {
                    // Play с layer 0 и normalized time 0
                    animator.Play(clip.name, 0, 0f);
                    animator.Update(0f); // Принудительное обновление
                    return true;
                }
            }
            return false;
        }

        // Старый метод TryPlayAnimation (оставляем для совместимости)
        private bool TryPlayAnimation(Animator animator, string animName)
        {
            return TryPlayAnimationWithUpdate(animator, animName);
        }

        private bool TrySetTrigger(Animator animator, string triggerName)
        {
            foreach (AnimatorControllerParameter param in animator.parameters)
            {
                if (param.type == AnimatorControllerParameterType.Trigger && 
                    param.name.Equals(triggerName, System.StringComparison.OrdinalIgnoreCase))
                {
                    animator.SetTrigger(param.name);
                    return true;
                }
            }
            return false;
        }

        private static HashSet<string> loggedCharacters = new HashSet<string>();
        private void LogAllAnimations(Animator animator)
        {
            if (loggedCharacters.Contains(currentCharacterName)) return;
            loggedCharacters.Add(currentCharacterName);

            Log.LogInfo($"[BeastMode] === Available animations for {currentCharacterName} ===");
            
            if (animator.runtimeAnimatorController == null)
            {
                Log.LogInfo($"  [No Animator Controller found]");
                return;
            }

            int count = 0;
            int slideCount = 0;
            foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
            {
                Log.LogInfo($"  - {clip.name} (length: {clip.length:F2}s)");
                count++;
                
                if (ContainsIgnoreCase(clip.name, "Slide") ||
                    ContainsIgnoreCase(clip.name, "Dive") ||
                    ContainsIgnoreCase(clip.name, "Roll"))
                {
                    slideCount++;
                }
            }
            
            Log.LogInfo($"[BeastMode] Total animations: {count}, Slide-related: {slideCount}");
            
            Log.LogInfo($"[BeastMode] === Available triggers for {currentCharacterName} ===");
            foreach (AnimatorControllerParameter param in animator.parameters)
            {
                if (param.type == AnimatorControllerParameterType.Trigger)
                {
                    Log.LogInfo($"  - Trigger: {param.name}");
                }
            }
            
            // === НОВОЕ: Логируем текущее состояние Animator ===
            Log.LogInfo($"[BeastMode] Animator layerCount: {animator.layerCount}, parameterCount: {animator.parameterCount}");
            if (animator.layerCount > 0)
            {
                var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                Log.LogInfo($"[BeastMode] Current state: {stateInfo.fullPathHash} (name: {stateInfo.fullPathHash})");
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

            if (lastProcessedBody != body)
            {
                headRenderersDisabled = false;
            }

            Transform head = FindHeadBone(model, body);
            
            var cam = Camera.main;
            if (cam == null) return;

            CameraSettings settings = BeastModePlugin.GetCurrentSettings();

            Vector3 headPos;
            
            if (head != null)
            {
                headPos = head.position 
                        + head.up * settings.upOffset
                        + cam.transform.forward * settings.forwardOffset;
                
                if (!headRenderersDisabled)
                {
                    DisableHeadRenderers(head);
                    headRenderersDisabled = true;
                }
            }
            else
            {
                Transform core = body.coreTransform;
                headPos = core != null 
                    ? core.position + Vector3.up * 1.7f 
                    : body.transform.position + Vector3.up * 1.7f;
            }
            
            lastProcessedBody = body;
            
            cam.transform.position = headPos;
            cam.fieldOfView = BeastModePlugin.ConfigFOV.Value;
        }

        private static Transform FindHeadBone(Transform model, CharacterBody body)
        {
            string characterName = body.baseNameToken;

            string[] characterSpecificPaths = GetCharacterSpecificPaths(characterName);
            foreach (string path in characterSpecificPaths)
            {
                Transform found = model.Find(path);
                if (found != null)
                {
                    BeastModePlugin.Log.LogInfo($"[BeastMode] Found head via specific path '{path}' for {characterName}");
                    return found;
                }
            }

            string[] headNames = new string[]
            {
                "Head", "head", "HEAD",
                "Bip001 Head", "Bip001 Head1",
                "Neck", "neck", "Neck1",
                "Bip001 Neck", "Bip001 Neck1",
                "Helmet", "helmet",
                "HeadEnd", "head_end"
            };

            foreach (string name in headNames)
            {
                Transform found = FindChildRecursive(model, name);
                if (found != null) return found;
            }
            return null;
        }

        private static string[] GetCharacterSpecificPaths(string characterName)
        {
            switch (characterName)
            {
                case "MERC_BODY_NAME":
                    return new string[]
                    {
                        "mdlMerc/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlMerc/Armature/Hips/Spine/Chest/UpperChest/Neck/Head",
                        "MercArmature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlMerc/Armature/Hips/Chest/Neck/Head"
                    };
                    
                case "TOOLBOT_BODY_NAME":
                    return new string[]
                    {
                        "mdlToolbot/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlToolbot/Armature/Hips/Chest/Head",
                        "mdlToolbot/Armature/Hips/Spine/Chest/Head",
                        "mdlToolbot/Armature/Hips/Chest/UpperChest/Head"
                    };
                    
                case "CROCO_BODY_NAME":
                    return new string[]
                    {
                        "mdlCroco/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlCroco/Armature/Hips/Spine/Chest/Neck/Head",
                        "mdlCroco/Armature/Hips/Chest/Neck/Head"
                    };
                    
                case "ENGI_BODY_NAME":
                    return new string[]
                    {
                        "mdlEngi/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlEngi/Armature/Hips/Spine/Chest/Neck/Head",
                        "mdlEngi/Armature/Hips/Chest/Neck/Head",
                        "mdlEngi/Armature/Hips/Chest/Head"
                    };
                    
                case "BANDIT2_BODY_NAME":
                    return new string[]
                    {
                        "mdlBandit2/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlBandit2/Armature/Hips/Spine/Chest/Neck/Head"
                    };
                    
                case "HUNTRESS_BODY_NAME":
                    return new string[]
                    {
                        "mdlHuntress/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlHuntress/Armature/Hips/Spine/Chest/Neck/Head"
                    };
                    
                case "COMMANDO_BODY_NAME":
                    return new string[]
                    {
                        "mdlCommandoDualies/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlCommandoDualies/Armature/Hips/Spine/Chest/Neck/Head"
                    };
                    
                case "MAGE_BODY_NAME":
                    return new string[]
                    {
                        "mdlMage/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlMage/Armature/Hips/Spine/Chest/Neck/Head"
                    };
                    
                case "LOADER_BODY_NAME":
                    return new string[]
                    {
                        "mdlLoader/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlLoader/Armature/Hips/Spine/Chest/Neck/Head"
                    };
                    
                case "SEEKER_BODY_NAME":
                    return new string[]
                    {
                        "mdlSeeker/Armature/Hips/Chest/UpperChest/Neck/Head",
                        "mdlSeeker/Armature/Hips/Spine/Chest/Neck/Head"
                    };
                    
                default:
                    return new string[0];
            }
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
            
            BeastModePlugin.Log.LogInfo($"[BeastMode] Disabled {renderers.Length} head renderers.");
        }
    }
}