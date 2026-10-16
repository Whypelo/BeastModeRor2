using BepInEx;
using BepInEx.Logging;
using RoR2;
using UnityEngine;
using HarmonyLib;

namespace BeastModeRor2
{
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

        internal int killsWithoutDamage = 0;
        internal bool isOverkillActive = false;
        private float slideTimer = 0f;
        private bool isSliding = false;
        internal CharacterBody playerBody;

        public void Awake()
        {
            Log = Logger;
            Log.LogInfo("[BeastMode] Initializing...");
            
            var harmony = new Harmony("com.melty.beastmoderor2");
            harmony.PatchAll();

            Log.LogInfo("[BeastMode] Beast Mode loaded successfully.");
        }

                internal void ActivateOverkill()
        {
            if (playerBody == null) return;
            isOverkillActive = true;
            
            // Изменяем базовые значения статов
            playerBody.baseDamage *= OVERKILL_DAMAGE_MULT;
            playerBody.baseMoveSpeed *= OVERKILL_SPEED_MULT;
            
            // Заставляем игру пересчитать итоговые характеристики
            playerBody.RecalculateStats();
            
            Log.LogInfo($"[BeastMode] OVERKILL ACTIVATED! Damage x{OVERKILL_DAMAGE_MULT}, Speed x{OVERKILL_SPEED_MULT}");
        }

        internal void DeactivateOverkill()
        {
            if (!isOverkillActive || playerBody == null) return;
            isOverkillActive = false;
            
            // Возвращаем базовые значения к исходным (делим на тот же множитель)
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

    // 1. ИСПРАВЛЕНО: Привязка к CharacterBody при его создании/спавне
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

    // 2. ИСПРАВЛЕНО: Патч теперь на HealthComponent.Die, а не CharacterBody.OnDeath
    [HarmonyPatch(typeof(HealthComponent), nameof(HealthComponent.Die))]
    public static class HealthComponentDiePatch
    {
        static void Postfix(HealthComponent __instance, DamageReport damageReport)
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || plugin.playerBody == null) return;
            
            // Игнорируем смерть самого игрока
            if (__instance.body != null && __instance.body.isPlayerControlled) return;
            
            // Проверяем, что убийца - это именно игрок (а не турель, падение или другой игрок)
            if (damageReport.attacker != null && damageReport.attacker == plugin.playerBody.gameObject)
            {
                plugin.killsWithoutDamage++;
                BeastModePlugin.Log.LogInfo($"[BeastMode] Kill streak: {plugin.killsWithoutDamage}/{BeastModePlugin.OVERKILL_THRESHOLD}");
                
                if (plugin.killsWithoutDamage >= BeastModePlugin.OVERKILL_THRESHOLD && !plugin.isOverkillActive)
                {
                    plugin.ActivateOverkill();
                }
            }
        }
    }

    // 3. Патч получения урона игроком (сброс серии)
    [HarmonyPatch(typeof(HealthComponent), nameof(HealthComponent.TakeDamage))]
    public static class HealthComponentTakeDamagePatch
    {
        static void Postfix(HealthComponent __instance, DamageInfo info)
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || plugin.playerBody == null) return;
            
            // Проверяем, что урон получен именно игроком
            if (__instance.body != plugin.playerBody) return;
            
            // Игнорируем нулевой урон или урон от самого себя (например, от своих же снарядов, если такое будет)
            if (info.damage <= 0 || info.attacker == __instance.gameObject) return;

            plugin.killsWithoutDamage = 0;
            
            if (plugin.isOverkillActive)
            {
                plugin.DeactivateOverkill();
            }
        }
    }

    // 4. Камера от первого лица
    [HarmonyPatch(typeof(CameraRigController), "Update")]
    public static class FirstPersonCameraPatch
    {
        static void Postfix(CameraRigController __instance)
        {
            if (__instance == null) return;
            
            var targetObj = __instance.target;
            if (targetObj == null) return;
            
            var body = targetObj.GetComponent<CharacterBody>();
            if (body == null || !body.isPlayerControlled) return;

            Transform model = body.modelLocator?.modelTransform;
            if (model == null) return;

            Transform head = model.Find("Head") ?? model.Find("Chest") ?? model;
            Vector3 headPos = head.position + head.up * 0.15f + head.forward * 0.05f;
            
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = headPos;
                // Если управление мышкой будет ломаться, закомментируйте строку ниже:
                cam.transform.rotation = Quaternion.LookRotation(body.inputBank.aimDirection, Vector3.up);
            }
        }
    }
}