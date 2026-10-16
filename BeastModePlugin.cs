using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using RoR2;
using UnityEngine;
using HarmonyLib;
using System.Collections.Generic;

namespace BeastModeRor2
{
    [BepInPlugin("com.melty.beastmoderor2", "Beast Mode", "0.1.0")]
    [BepInProcess("Risk of Rain 2.exe")]
    public class BeastModePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        
        // Настройки из конфига
        internal static ConfigEntry<float> ConfigFOV;
        internal static ConfigEntry<float> ConfigForwardOffset;
        internal static ConfigEntry<float> ConfigUpOffset;
        
        public const int OVERKILL_THRESHOLD = 3;
        public const float OVERKILL_DAMAGE_MULT = 1.5f;
        public const float OVERKILL_SPEED_MULT = 1.2f;
        public const float SLIDE_SPEED = 20f;
        public const float SLIDE_DURATION = 0.4f;

        internal int killsWithoutDamage = 0;
        internal bool isOverkillActive = false;
        private float slideTimer = 0f;
        private bool isSliding = false;
        internal CharacterBody playerBody;

        public void Awake()
        {
            Log = Logger;
            Log.LogInfo("[BeastMode] Initializing...");
            
            // Инициализация настроек из конфига
            ConfigFOV = Config.Bind<float>("Camera", "FieldOfView", 90f, 
                new ConfigDescription("Поле зрения камеры (FOV). Стандартное значение 90.", 
                new AcceptableValueRange<float>(30f, 120f)));
            
            ConfigForwardOffset = Config.Bind<float>("Camera", "ForwardOffset", 0.15f, 
                new ConfigDescription("Смещение камеры вперёд от головы (чтобы не видеть тело).", 
                new AcceptableValueRange<float>(0f, 1f)));
            
            ConfigUpOffset = Config.Bind<float>("Camera", "UpOffset", 0.05f, 
                new ConfigDescription("Смещение камеры вверх от головы.", 
                new AcceptableValueRange<float>(-0.5f, 0.5f)));
            
            var harmony = new Harmony("com.melty.beastmoderor2");
            harmony.PatchAll();

            Log.LogInfo("[BeastMode] Beast Mode loaded successfully.");
        }

        internal void ActivateOverkill()
        {
            if (playerBody == null) return;
            isOverkillActive = true;
            
            playerBody.baseDamage *= OVERKILL_DAMAGE_MULT;
            playerBody.baseMoveSpeed *= OVERKILL_SPEED_MULT;
            
            playerBody.RecalculateStats();
            
            Log.LogInfo($"[BeastMode] OVERKILL ACTIVATED! Damage x{OVERKILL_DAMAGE_MULT}, Speed x{OVERKILL_SPEED_MULT}");
        }

        internal void DeactivateOverkill()
        {
            if (!isOverkillActive || playerBody == null) return;
            isOverkillActive = false;
            
            playerBody.baseDamage /= OVERKILL_DAMAGE_MULT;
            playerBody.baseMoveSpeed /= OVERKILL_SPEED_MULT;
            
            playerBody.RecalculateStats();
            killsWithoutDamage = 0;
            Log.LogInfo("[BeastMode] Overkill deactivated. Stats normalized.");
        }

        public void Update()
        {
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
                    BeastModePlugin.Log.LogInfo("[BeastMode] Player body attached.");
                }
            }
        }
    }

    // Словарь для хранения последнего атакующего для каждого HealthComponent
    public static class LastAttackerTracker
    {
        public static Dictionary<HealthComponent, GameObject> LastAttackers = new Dictionary<HealthComponent, GameObject>();
    }

    // Патч получения урона - сохраняем информацию об атакующем
    [HarmonyPatch(typeof(HealthComponent), "TakeDamage")]
    public static class HealthComponentTakeDamagePatch
    {
        // ИСПРАВЛЕНО: параметр теперь называется damageInfo, а не info
        static void Postfix(HealthComponent __instance, DamageInfo damageInfo)
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || plugin.playerBody == null) return;
            
            // Сохраняем информацию об атакующем для этого HealthComponent
            if (damageInfo.attacker != null)
            {
                LastAttackerTracker.LastAttackers[__instance] = damageInfo.attacker;
            }
            
            // Проверяем, что урон получен именно игроком
            if (__instance.body != plugin.playerBody) return;
            if (damageInfo.damage <= 0 || damageInfo.attacker == __instance.gameObject) return;

            plugin.killsWithoutDamage = 0;
            
            if (plugin.isOverkillActive)
            {
                plugin.DeactivateOverkill();
            }
        }
    }

    // Патч смерти - используем сохраненную информацию об атакующем
    [HarmonyPatch(typeof(HealthComponent), "Die")]
    public static class HealthComponentDiePatch
    {
        static void Postfix(HealthComponent __instance, bool noCorpse)
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || plugin.playerBody == null) return;
            
            // Игнорируем смерть самого игрока
            if (__instance.body != null && __instance.body.isPlayerControlled) return;
            
            // Получаем сохраненного атакующего
            if (LastAttackerTracker.LastAttackers.TryGetValue(__instance, out GameObject attacker))
            {
                // Проверяем, что убийца - это именно игрок
                if (attacker == plugin.playerBody.gameObject)
                {
                    plugin.killsWithoutDamage++;
                    BeastModePlugin.Log.LogInfo($"[BeastMode] Kill streak: {plugin.killsWithoutDamage}/{BeastModePlugin.OVERKILL_THRESHOLD}");
                    
                    if (plugin.killsWithoutDamage >= BeastModePlugin.OVERKILL_THRESHOLD && !plugin.isOverkillActive)
                    {
                        plugin.ActivateOverkill();
                    }
                }
                
                // Очищаем запись после использования
                LastAttackerTracker.LastAttackers.Remove(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(CameraRigController), "LateUpdate")]
    public static class FirstPersonCameraPatch
    {
        private static bool headRenderersDisabled = false;
        private static CharacterBody lastProcessedBody = null;
        private static Transform disabledHeadTransform = null;

        static void Postfix(CameraRigController __instance)
        {
            if (__instance == null) return;
            
            var targetObj = __instance.target;
            if (targetObj == null) return;
            
            var body = targetObj.GetComponent<CharacterBody>();
            if (body == null || !body.isPlayerControlled) return;

            Transform model = body.modelLocator?.modelTransform;
            if (model == null) return;

            // Ищем голову или шею
            Transform head = FindHeadBone(model);
            
            Vector3 headPos;
            
            if (head != null)
            {
                // Позиция головы + смещение вверх + смещение вперёд по направлению взгляда
                headPos = head.position 
                        + head.up * BeastModePlugin.ConfigUpOffset.Value
                        + body.inputBank.aimDirection.normalized * BeastModePlugin.ConfigForwardOffset.Value;
                
                // === КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ: поворачиваем модель в направлении взгляда ===
                // Это нужно, чтобы когда игрок стоит на месте и крутит мышкой,
                // модель тоже поворачивалась и не было видно внутренностей.
                RotateModelToCamera(body, model);
                
                // Отключаем рендереры головы один раз при смене персонажа
                if (!headRenderersDisabled || lastProcessedBody != body)
                {
                    DisableHeadRenderers(head);
                    headRenderersDisabled = true;
                    lastProcessedBody = body;
                    disabledHeadTransform = head;
                }
            }
            else
            {
                // Fallback: используем центр персонажа + высота глаз
                Transform core = body.coreTransform;
                headPos = core != null 
                    ? core.position + Vector3.up * 1.7f 
                    : body.transform.position + Vector3.up * 1.7f;
                
                RotateModelToCamera(body, model);
            }
            
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = headPos;
                cam.transform.rotation = Quaternion.LookRotation(body.inputBank.aimDirection, Vector3.up);
                
                // Применяем FOV из конфига
                cam.fieldOfView = BeastModePlugin.ConfigFOV.Value;
            }
        }

        // Поворот модели персонажа в направлении взгляда камеры
        private static void RotateModelToCamera(CharacterBody body, Transform model)
        {
            if (body.inputBank == null) return;
            
            Vector3 aimDir = body.inputBank.aimDirection;
            // Игнорируем вертикальную составляющую - модель должна стоять ровно
            aimDir.y = 0f;
            if (aimDir.sqrMagnitude < 0.001f) return;
            
            // Создаём кватернион поворота только вокруг оси Y
            Quaternion targetRotation = Quaternion.LookRotation(aimDir, Vector3.up);
            
            // Плавно поворачиваем модель (чтобы не было резких дёрганий)
            model.rotation = Quaternion.Slerp(model.rotation, targetRotation, Time.deltaTime * 15f);
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
            
            // Дополнительно отключаем рендереры на всём торсе, если они видны
            // Ищем кость груди/туловища
            Transform chest = FindChildRecursive(head.parent != null ? head.parent : head, "Chest");
            if (chest == null) chest = FindChildRecursive(head.parent != null ? head.parent : head, "Spine");
            
            if (chest != null)
            {
                Renderer[] chestRenderers = chest.GetComponentsInChildren<Renderer>();
                foreach (Renderer renderer in chestRenderers)
                {
                    // Отключаем только те, что находятся очень близко к голове
                    if (Vector3.Distance(renderer.transform.position, head.position) < 0.5f)
                    {
                        renderer.enabled = false;
                    }
                }
            }
            
            BeastModePlugin.Log.LogInfo($"[BeastMode] Disabled head renderers.");
        }
    }
}