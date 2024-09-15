using BepInEx;
using BepInEx.Configuration;
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
    enum MidAirExplosions
    {
        Off = -1,
        OnlyTooHigh,
        Always
    }

    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.jetpackfixes", PLUGIN_NAME = "Jetpack Fixes", PLUGIN_VERSION = "1.5.0";
        internal static new ManualLogSource Logger;

        internal static ConfigEntry<MidAirExplosions> configMidAirExplosions;
        internal static ConfigEntry<bool> configTransferMomentum;

        void Awake()
        {
            Logger = base.Logger;

            configMidAirExplosions = Config.Bind(
                "Misc",
                "MidAirExplosions",
                MidAirExplosions.OnlyTooHigh,
                "When should high speeds (exceeding 50u/s, vanilla's \"speed limit\") explode the jetpack?\n" +
                "\"Off\" will only explode when you crash into something solid.\n" + 
                "\"OnlyTooHigh\" will explode if you are flying too fast, while you are also *extremely* high above the terrain.\n" +
                "\"Always\" will explode any time you are flying too fast. (Most similar to vanilla's behavior)");

            configTransferMomentum = Config.Bind(
                "Misc",
                "TransferMomentum",
                false,
                "When dropping the jetpack, instead of immediately coming to a stop, you will maintain the same direction and speed.");

            // migrate legacy config
            if (configMidAirExplosions.Value == MidAirExplosions.OnlyTooHigh)
            {
                bool becomeFirework = Config.Bind("Misc", "BecomeFirework", true, "Legacy setting, use \"MidAirExplosions\" instead").Value;

                if (!becomeFirework)
                    configMidAirExplosions.Value = MidAirExplosions.Off;

                Config.Remove(Config["Misc", "BecomeFirework"].Definition);
                Config.Save();
            }

            new Harmony(PLUGIN_GUID).PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    class JetpackFixesPatches
    {
        // sort of arbitrary
        // ~110 Y is roughly how high you can get by the time you reach instant death speed, if you go straight up from the ship's floor in one trip
        const float SAFE_HEIGHT = 110.55537f;

        const float MIN_DEATH_SPEED = 50f;
        // Since vanilla requires speed to exceed the RaycastHit's distance (which has a maximum of 4) plus 50
        const float MAX_DEATH_SPEED = 54f;

        static EnemyType flowerSnakeEnemy;
        //static Collider localPlayerCube;

        static readonly FieldInfo JETPACK_ACTIVATED = AccessTools.Field(typeof(JetpackItem), "jetpackActivated");

        [HarmonyPatch(typeof(JetpackItem), nameof(JetpackItem.Update))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TransJetpackUpdate(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            LayerMask allPlayersCollideWithMask = -1111789641; // StartOfRound.allPlayersCollideWithMask
            // All the player's MapHazards and DecalStickableSurface colliders are marked as triggers, so they should be ok
            allPlayersCollideWithMask &= ~(1 << LayerMask.NameToLayer("PlaceableShipObjects"));
            // Terrain was removed in v56, add it back so we can crash into trees
            allPlayersCollideWithMask |= (1 << LayerMask.NameToLayer("Terrain"));
            // As of v64, belt bags now attach an "InteractableObject" layer object to the player, which can also be crashed into
            allPlayersCollideWithMask &= ~(1 << LayerMask.NameToLayer("InteractableObject"));

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
                    Plugin.Logger.LogDebug("Transpiler: Replace layer mask with custom");
                }
            }

            return codes;
        }

        [HarmonyPatch(typeof(JetpackItem), nameof(JetpackItem.Update))]
        [HarmonyPostfix]
        static void PostJetpackUpdate(JetpackItem __instance, Vector3 ___forces, bool ___jetpackActivated, float ___jetpackPower)
        {
            if (GameNetworkManager.Instance?.localPlayerController == null)
                return;

            if (__instance.playerHeldBy == GameNetworkManager.Instance.localPlayerController && !__instance.playerHeldBy.isPlayerDead && ___jetpackActivated && __instance.playerHeldBy.jetpackControls)
            {
                if (___jetpackPower > 10f)
                {
                    float velocity = ___forces.magnitude;
                    if (velocity > MIN_DEATH_SPEED)
                    {
                        // Kills the player at excessive speed. Basically replicates vanilla's behavior (with less physics jank)
                        // NEW: Config setting to only apply this at extreme heights
                        if (velocity > MAX_DEATH_SPEED && (Plugin.configMidAirExplosions.Value == MidAirExplosions.Always || (Plugin.configMidAirExplosions.Value == MidAirExplosions.OnlyTooHigh && __instance.transform.position.y > SAFE_HEIGHT)))
                        {
                            __instance.playerHeldBy.KillPlayer(___forces, true, CauseOfDeath.Gravity);
                            if (Plugin.configMidAirExplosions.Value == MidAirExplosions.Always)
                                Plugin.Logger.LogInfo("Player killed from flying too fast");
                            else
                                Plugin.Logger.LogInfo($"Player killed from flying too high too fast (Altitude: {__instance.transform.position.y} > {SAFE_HEIGHT})");
                        }
                        // Kills the player if they try to slide across the ground while moving too fast
                        // (This is still a collision taking place while moving at instant-death speeds)
                        else if (__instance.playerHeldBy.thisController.isGrounded)
                        {
                            __instance.playerHeldBy.KillPlayer(___forces, true, CauseOfDeath.Gravity);
                            Plugin.Logger.LogInfo("Player killed from touching ground while flying too fast");
                        }
                    }
                }

                // Regain full directional control when activating jetpack after tulip snake takeoff
                if (__instance.playerHeldBy != null && __instance.playerHeldBy.maxJetpackAngle >= 0f && __instance.playerHeldBy.maxJetpackAngle < 360f)
                {
                    __instance.playerHeldBy.maxJetpackAngle = float.MaxValue; //-1f;
                    __instance.playerHeldBy.jetpackRandomIntensity = 60f; //0f;
                    Plugin.Logger.LogInfo("Uncap player rotation (using jetpack while tulip snakes riding)");
                }
            }

            // Fixes inverted jetpack battery
            __instance.isBeingUsed = ___jetpackActivated;
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayer))]
        [HarmonyPrefix]
        static void PrePlayerDamaged(PlayerControllerB __instance, ref int damageNumber, CauseOfDeath causeOfDeath)
        {
            // Player crashed into something while travelling at a speed past the intended instant-death threshold
            if (causeOfDeath == CauseOfDeath.Gravity && __instance == GameNetworkManager.Instance.localPlayerController && __instance.jetpackControls && __instance.averageVelocity >= MIN_DEATH_SPEED)
            {
                Plugin.Logger.LogInfo($"Player took {damageNumber} \"Gravity\" damage while flying too fast; should be instant death");
                damageNumber = Mathf.Max(100, __instance.health);
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

                /*if (localPlayerCube == null)
                {
                    localPlayerCube = __instance.playerHeldBy.transform.Find("Misc/Cube")?.GetComponent<Collider>();
                    if (localPlayerCube?.gameObject.layer == 26)
                        Plugin.Logger.LogInfo("Cached player's \"PlaceableShipObjects\" collider");
                    else
                    {
                        localPlayerCube = null;
                        Plugin.Logger.LogWarning("Error fetching player's \"PlaceableShipObjects\" collider");
                    }
                }*/
            }
            else
            {
                __instance.jetpackAudio.dopplerLevel = 1f;
                __instance.jetpackBeepsAudio.dopplerLevel = 1f;
                Plugin.Logger.LogInfo("Jetpack held by other player, enable doppler effect");
            }

            // hopefully fix the jetpack not responding to inputs
            __instance.useCooldown = 0f;
        }

        [HarmonyPatch(typeof(JetpackItem), "DeactivateJetpack")]
        [HarmonyPostfix]
        static void PostDeactivateJetpack(PlayerControllerB ___previousPlayerHeldBy)
        {
            if (___previousPlayerHeldBy != GameNetworkManager.Instance.localPlayerController || !___previousPlayerHeldBy.disablingJetpackControls)
                return;

            // Try to optimize by not performing this check unless there are tulip snakes on the map
            if (flowerSnakeEnemy != null && flowerSnakeEnemy.numberSpawned > 0)
            {
                int snakesLeft = flowerSnakeEnemy.numberSpawned;
                foreach (EnemyAI enemyAI in RoundManager.Instance.SpawnedEnemies)
                {
                    FlowerSnakeEnemy tulipSnake = enemyAI as FlowerSnakeEnemy;
                    if (tulipSnake != null)
                    {
                        snakesLeft--;
                        // Verify if there is a living tulip snake clung to the player and flapping its wings
                        if (!tulipSnake.isEnemyDead && tulipSnake.clingingToPlayer == ___previousPlayerHeldBy && tulipSnake.flightPower > 0f)
                        {
                            tulipSnake.clingingToPlayer.disablingJetpackControls = false;
                            // Can't set maxJetpackAngle after player has been flying with free rotation, or their angle could lock up
                            // However, jetpackRandomIntensity is capped by maxJetpackAngle, so it needs to be set to an arbitrarily high value
                            tulipSnake.clingingToPlayer.maxJetpackAngle = float.MaxValue; //60f;
                            tulipSnake.clingingToPlayer.jetpackRandomIntensity = 60f; //120f;
                            Plugin.Logger.LogInfo("Jetpack disabled, but tulip snake is still carrying");
                            return;
                        }

                        if (snakesLeft <= 0)
                            return;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(FlowerSnakeEnemy), "SetFlappingLocalClient")]
        [HarmonyPostfix]
        static void PostSetFlappingLocalClient(FlowerSnakeEnemy __instance, bool isMainSnake)
        {
            if (!isMainSnake || __instance.clingingToPlayer != GameNetworkManager.Instance.localPlayerController || !__instance.clingingToPlayer.disablingJetpackControls)
                return;

            for (int i = 0; i < __instance.clingingToPlayer.ItemSlots.Length; i++)
            {
                // If the item is equipped...
                if (__instance.clingingToPlayer.ItemSlots[i] == null || __instance.clingingToPlayer.ItemSlots[i].isPocketed)
                    continue;

                JetpackItem jetpack = __instance.clingingToPlayer.ItemSlots[i] as JetpackItem;
                // ... and is a jetpack that's activated...
                if (jetpack != null && (bool)JETPACK_ACTIVATED.GetValue(jetpack))
                {
                    __instance.clingingToPlayer.disablingJetpackControls = false;
                    __instance.clingingToPlayer.maxJetpackAngle = -1f;
                    __instance.clingingToPlayer.jetpackRandomIntensity = 0f;
                    Plugin.Logger.LogInfo("Player still using jetpack when tulip snake dropped; re-enable flight controls");
                    return;
                }
            }
        }

        [HarmonyPatch(typeof(FlowerSnakeEnemy), nameof(FlowerSnakeEnemy.Start))]
        [HarmonyPostfix]
        static void PostTulipSnakeStart(FlowerSnakeEnemy __instance)
        {
            if (flowerSnakeEnemy == null)
                flowerSnakeEnemy = __instance.enemyType;
        }

        [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Disconnect))]
        [HarmonyPostfix]
        static void GameNetworkManagerPostDisconnect()
        {
            flowerSnakeEnemy = null;
        }

        [HarmonyPatch(typeof(JetpackItem), nameof(JetpackItem.DiscardItem))]
        [HarmonyPostfix]
        static void PostDropJetpack(JetpackItem __instance, PlayerControllerB ___previousPlayerHeldBy, ref Vector3 ___forces)
        {
            if (Plugin.configTransferMomentum.Value && !___previousPlayerHeldBy.isPlayerDead && ___previousPlayerHeldBy.jetpackControls && ___forces.magnitude > 0f)
            {
                ___previousPlayerHeldBy.externalForceAutoFade += new Vector3(___forces.x, ___forces.y * __instance.verticalMultiplier, ___forces.z);
                Plugin.Logger.LogInfo("Player dropped jetpack while flying, fling them!");
            }
            ___forces = Vector3.zero;
        }

        /*[HarmonyPatch(typeof(JetpackItem), "ActivateJetpack")]
        [HarmonyPostfix]
        static void PostActivateJetpack(JetpackItem __instance, Vector3 ___forces)
        {
            if (__instance.playerHeldBy == GameNetworkManager.Instance.localPlayerController && localPlayerCube != null && localPlayerCube.enabled && __instance.playerHeldBy.jetpackControls)
                localPlayerCube.enabled = false;
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DisableJetpackControlsLocally))]
        [HarmonyPostfix]
        static void FlightControlsDisabled(PlayerControllerB __instance)
        {
            if (__instance == GameNetworkManager.Instance.localPlayerController && localPlayerCube != null && !localPlayerCube.enabled)
                localPlayerCube.enabled = true;
        }*/

        [HarmonyPatch(typeof(JetpackItem), "SetJetpackAudios")]
        [HarmonyPrefix]
        static bool NewJetpackAudio(JetpackItem __instance, ref bool ___jetpackActivated, ref float ___noiseInterval)
        {
            if (___jetpackActivated)
            {
                if (___noiseInterval >= 0.5f)
                {
                    ___noiseInterval = 0f;
                    RoundManager.Instance.PlayAudibleNoise(__instance.transform.position, 25f, 0.85f, 0, __instance.playerHeldBy.isInHangarShipRoom && StartOfRound.Instance.hangarDoorsClosed, 41);
                }
                else
                    ___noiseInterval += Time.deltaTime;

                if (__instance.insertedBattery.charge < 0.15f)
                {
                    if (__instance.jetpackBeepsAudio.clip != __instance.jetpackLowBatteriesSFX)
                    {
                        __instance.jetpackBeepsAudio.Stop();
                        __instance.jetpackBeepsAudio.clip = __instance.jetpackLowBatteriesSFX;
                        //__instance.jetpackBeepsAudio.loop = true;
                    }

                    if (!__instance.jetpackBeepsAudio.isPlaying)
                        __instance.jetpackBeepsAudio.Play();
                }
                else
                {
                    if (__instance.jetpackBeepsAudio.clip != __instance.jetpackWarningBeepSFX)
                    {
                        __instance.jetpackBeepsAudio.Stop();
                        __instance.jetpackBeepsAudio.clip = __instance.jetpackWarningBeepSFX;
                        //__instance.jetpackBeepsAudio.loop = false;
                    }

                    // maybe 7m? idk
                    if (Physics.CheckSphere(__instance.transform.position, 6f, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
                    {
                        if (!__instance.jetpackBeepsAudio.isPlaying)
                            __instance.jetpackBeepsAudio.Play();
                    }
                    else
                        __instance.jetpackBeepsAudio.Stop();
                }
            }
            else
                __instance.jetpackBeepsAudio.Stop();

            return false;
        }
    }
}