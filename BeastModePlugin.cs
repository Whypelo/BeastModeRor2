using BepInEx;
using BepInEx.Logging;
using RoR2;
using UnityEngine;
using HarmonyLib;
using System;
using System.Reflection;

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

            Log.LogInfo("[BeastMode] Beast Mode loaded.");
        }

        internal void ActivateOverkill()
        {
            isOverkillActive = true;
            
            var damageField = typeof(CharacterBody).GetField("damage", BindingFlags.NonPublic | BindingFlags.Instance);
            var speedField = typeof(CharacterBody).GetField("moveSpeed", BindingFlags.NonPublic | BindingFlags.Instance);
            
            if (damageField != null && playerBody != null)
            {
                float currentDamage = (float)damageField.GetValue(playerBody);
                damageField.SetValue(playerBody, currentDamage * OVERKILL_DAMAGE_MULT);
            }
            
            if (speedField != null && playerBody != null)
            {
                float currentSpeed = (float)speedField.GetValue(playerBody);
                speedField.SetValue(playerBody, currentSpeed * OVERKILL_SPEED_MULT);
            }
            
            Log.LogInfo($"[BeastMode] OVERKILL ACTIVATED!");
        }

        internal void DeactivateOverkill()
        {
            if (!isOverkillActive || playerBody == null) return;
            isOverkillActive = false;
            
            playerBody.RecalculateStats();
            
            killsWithoutDamage = 0;
            Log.LogInfo("[BeastMode] Overkill deactivated.");
        }

        public void Update()
        {
            if (playerBody == null) return;

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

    [HarmonyPatch(typeof(CharacterBody), "OnDeath")]
    public static class CharacterBodyDeathPatch
    {
        static void Postfix(CharacterBody __instance)
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || plugin.playerBody == null) return;
            
            if (__instance == null) return;
            if (__instance.teamComponent.teamIndex == TeamIndex.Player) return;
            
            // Упрощенная проверка - считаем любые убийства врагов
            plugin.killsWithoutDamage++;
            BeastModePlugin.Log.LogInfo($"[BeastMode] Kill streak: {plugin.killsWithoutDamage}/{BeastModePlugin.OVERKILL_THRESHOLD}");
            
            if (plugin.killsWithoutDamage >= BeastModePlugin.OVERKILL_THRESHOLD && !plugin.isOverkillActive)
            {
                plugin.ActivateOverkill();
            }
        }
    }

    [HarmonyPatch(typeof(HealthComponent), "TakeDamage")]
    public static class HealthComponentTakeDamagePatch
    {
        static void Postfix(HealthComponent __instance, DamageInfo info)
        {
            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<BeastModePlugin>();
            if (plugin == null || plugin.playerBody == null) return;
            
            if (__instance.body != plugin.playerBody) return;
            if (info.damage <= 0 || info.attacker == __instance.gameObject) return;

            plugin.killsWithoutDamage = 0;
            
            if (plugin.isOverkillActive)
            {
                plugin.DeactivateOverkill();
            }
        }
    }

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
                cam.transform.rotation = Quaternion.LookRotation(head.forward, Vector3.up);
            }
        }
    }
}