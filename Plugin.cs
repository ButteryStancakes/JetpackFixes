using BepInEx;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace JetpackFixes
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.jetpackfixes", PLUGIN_NAME = "Jetpack Fixes", PLUGIN_VERSION = "1.2.0";
        internal static new ManualLogSource Logger;

        void Awake()
        {
            Logger = base.Logger;

            new Harmony(PLUGIN_GUID).PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    class JetpackFixesPatches
    {
        [HarmonyPatch(typeof(JetpackItem), nameof(JetpackItem.Update))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TransJetpackUpdate(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            LayerMask allPlayersCollideWithMask = -4493385; // StartOfRound.allPlayersCollideWithMask
            // all the player's MapHazards and DecalStickableSurface colliders are marked as triggers so they should be ok in v50
            allPlayersCollideWithMask &= ~(1 << LayerMask.NameToLayer("PlaceableShipObjects"));
            // boundaries for i-1 and i+3
            for (int i = 1; i < codes.Count - 3; i++)
            {
                // The player gets locked in place if jetpackPower > 10 and they touch the ground.
                // I think the intention was that the player stays grounded until jetpackPower is greater than 10,
                // since this code was added in the same beta patch that nerfed jetpack's takeoff efficiency,
                // but that would require the comparison to be reversed (jetpackPower <= 10)
                if (codes[i].opcode == OpCodes.Ldc_R4 && (float)codes[i].operand == 10f && codes[i - 1].opcode == OpCodes.Ldfld && (FieldInfo)codes[i - 1].operand == typeof(JetpackItem).GetField("jetpackPower", BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    codes[i + 1].opcode = OpCodes.Bgt_Un;
                    Plugin.Logger.LogDebug("Transpiler: Reverse jetpackPower comparison on isGrounded check (allows for sliding)");
                }
                // Reduce range of raycast (and remove redundancy with distance check)
                else if (codes[i].opcode == OpCodes.Ldflda && (FieldInfo)codes[i].operand == typeof(JetpackItem).GetField("rayHit", BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (codes[i + 1].opcode == OpCodes.Ldc_R4 && (float)codes[i + 1].operand == 25f)
                    {
                        codes[i + 1].operand = 4f;
                        Plugin.Logger.LogDebug("Transpiler: Reduce raycast range from 25 to 4");
                    }
                    else if (codes[i + 2].opcode == OpCodes.Ldc_R4 && (float)codes[i + 2].operand == 4f)
                    {
                        for (int j = i + 3; j >= i - 1; j--)
                            codes.RemoveAt(j);
                        Plugin.Logger.LogDebug("Transpiler: Remove 4 unit distance check (redundant)");
                    }
                }
                // Replace raycast layer with the new layer mask (prevents player from colliding with self)
                else if (codes[i].opcode == OpCodes.Ldfld && (FieldInfo)codes[i].operand == typeof(StartOfRound).GetField(nameof(StartOfRound.allPlayersCollideWithMask), BindingFlags.Instance | BindingFlags.Public))
                {
                    codes[i].opcode = OpCodes.Ldc_I4;
                    codes[i].operand = (int)allPlayersCollideWithMask;
                    codes.RemoveAt(i - 1);
                    Plugin.Logger.LogDebug("Transpiler: Player will no longer collide with themselves");
                }
            }

            return codes;
        }

        [HarmonyPatch(typeof(JetpackItem), nameof(JetpackItem.Update))]
        [HarmonyPrefix]
        static void PreJetpackUpdate(JetpackItem __instance, ref Vector3 ___forces, bool ___jetpackActivated, float ___jetpackPower)
        {
            if (__instance.playerHeldBy == GameNetworkManager.Instance.localPlayerController && !__instance.playerHeldBy.isPlayerDead && ___jetpackActivated && ___jetpackPower > 10f && __instance.playerHeldBy.jetpackControls && ___forces.magnitude > 50f)
            {
                // NEW: kills the player if they try to slide across the ground while moving too fast
                // (this is still a collision taking place while moving at instant-death speeds)
                if (__instance.playerHeldBy.thisController.isGrounded)
                {
                    __instance.playerHeldBy.KillPlayer(___forces, true, CauseOfDeath.Gravity);
                    Plugin.Logger.LogInfo("Player killed from touching ground while flying too fast");
                }
                // TODO: kills the player if exceeding safe speed at certain altitude (config setting)
            }
        }

        [HarmonyPatch(typeof(JetpackItem), nameof(JetpackItem.Update))]
        [HarmonyPostfix]
        static void PostJetpackUpdate(JetpackItem __instance, bool ___jetpackActivated)
        {
            __instance.isBeingUsed = ___jetpackActivated;
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayer))]
        [HarmonyPrefix]
        static void PrePlayerDamaged(PlayerControllerB __instance, ref int damageNumber, CauseOfDeath causeOfDeath)
        {
            // player crashed into something while travelling at a speed past the intended instant-death threshold
            if (causeOfDeath == CauseOfDeath.Gravity && __instance == GameNetworkManager.Instance.localPlayerController && __instance.jetpackControls && __instance.averageVelocity >= 50f)
            {
                Plugin.Logger.LogInfo($"Player took {damageNumber} \"Gravity\" damage while flying too fast; override with 100 (instant death)");
                damageNumber = 100;
            }
        }

        [HarmonyPatch(typeof(JetpackItem), nameof(JetpackItem.EquipItem))]
        [HarmonyPostfix]
        static void PostEquipJetpack(JetpackItem __instance)
        {
            // Doppler effect is only meant to apply to audio waves travelling towards or away from the listener (not a jetpack strapped to your back)
            if (__instance.playerHeldBy == GameNetworkManager.Instance.localPlayerController)
            {
                __instance.jetpackAudio.dopplerLevel = 0f;
                __instance.jetpackBeepsAudio.dopplerLevel = 0f;
                Plugin.Logger.LogInfo("Jetpack held by you, disable doppler effect");
            }
            else
            {
                __instance.jetpackAudio.dopplerLevel = 1f;
                __instance.jetpackBeepsAudio.dopplerLevel = 1f;
                Plugin.Logger.LogInfo("Jetpack held by other player, enable doppler effect");
            }
        }
    }
}