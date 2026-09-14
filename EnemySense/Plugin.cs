using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EnemySense
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "KZ.CreatureSense";
        public const string PluginName = "EnemySense";
        public const string PluginVersion = "2.0.0";

        internal static ConfigEntry<float> BaseDetectionRange;
        internal static ConfigEntry<float> SkillMultiplier;
        internal static ConfigEntry<float> StaminaDrain;
        internal static ConfigEntry<bool> ShowMessage;
        internal static ConfigEntry<bool> ShowVisual;
        internal static ConfigEntry<bool> PlayAudio;
        internal static ConfigEntry<bool> ShowMinimapIcons;
        internal static ConfigEntry<bool> AdditionalZoom;
        internal static ConfigEntry<KeyboardShortcut> CustomKeybind;
        internal static ConfigEntry<string> CustomMessage;
        internal static ConfigEntry<string> MessageColor;

        private Harmony _harmony;

        private void Awake()
        {
            BaseDetectionRange = Config.Bind("Adjustments", "Base Detection Range", 30f,
                "How far away creature health bars can be revealed. Vanilla used 30m when this mod was created.");
            SkillMultiplier = Config.Bind("Adjustments", "Skill Multiplier", 1f,
                "Multiplier for the extra range granted by Sneak skill. 1.0 adds up to 30m at Sneak 100.");
            StaminaDrain = Config.Bind("Adjustments", "Stamina Drain", 70f,
                "Stamina consumed by a sonar ping. Set to 0 for no stamina cost.");
            ShowMessage = Config.Bind("Features", "Show Message", true,
                "Show how many creatures were detected.");
            ShowVisual = Config.Bind("Features", "Visual Effect", true,
                "Play the sonar visual effect.");
            PlayAudio = Config.Bind("Features", "Audio Effect", true,
                "Play the sonar sound effect.");
            ShowMinimapIcons = Config.Bind("Features", "Minimap Icons", true,
                "Show detected creatures on the minimap for as long as their health bar is revealed.");
            AdditionalZoom = Config.Bind("Features", "Additional Zoom", true,
                "Allows two additional zoom steps on the minimap.");
            CustomKeybind = Config.Bind("Features", "Custom Keybind", new KeyboardShortcut(KeyCode.None),
                "Optional dedicated sonar key. Leave as None to use the original crouch + Walk key combination.");
            CustomMessage = Config.Bind("Features", "Custom Message", "",
                "Optional message. Use # as the detected-creature count.");
            MessageColor = Config.Bind("Adjustments", "Message Color", "#bbddff",
                "Hex color used for the detection message.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded");
        }

        private void OnDestroy()
        {
            Sonar.RemoveAllPins();
            _harmony?.UnpatchSelf();
        }
    }

    internal static class Sonar
    {
        private sealed class TrackedPin
        {
            public Character Character;
            public Minimap.PinData Pin;
            public float ExpireAt;
        }

        private static readonly Dictionary<Character, TrackedPin> Pins = new Dictionary<Character, TrackedPin>();
        private static readonly FieldInfo MaxShowDistanceField = AccessTools.Field(typeof(EnemyHud), "m_maxShowDistance");
        private static readonly FieldInfo HoverShowDurationField = AccessTools.Field(typeof(EnemyHud), "m_hoverShowDuration");
        private static readonly FieldInfo HudsField = AccessTools.Field(typeof(EnemyHud), "m_huds");
        private static readonly MethodInfo ShowHudMethod = AccessTools.Method(typeof(EnemyHud), "ShowHud");

        internal static float GetRange(Player player)
        {
            if (player == null)
                return Plugin.BaseDetectionRange.Value;

            float sneak = Mathf.Clamp(player.GetSkillLevel(Skills.SkillType.Sneak), 0f, 100f);
            return Mathf.Max(0f, Plugin.BaseDetectionRange.Value + (sneak / 100f) * 30f * Plugin.SkillMultiplier.Value);
        }

        internal static void ApplyDetectionRange(EnemyHud hud)
        {
            if (hud == null || MaxShowDistanceField == null)
                return;

            MaxShowDistanceField.SetValue(hud, GetRange(Player.m_localPlayer));
        }

        internal static void Ping(Player player)
        {
            if (player == null || EnemyHud.instance == null)
                return;

            float cost = Mathf.Max(0f, Plugin.StaminaDrain.Value);
            if (cost > 0f)
            {
                if (!player.HaveStamina(cost))
                {
                    if (Hud.instance != null)
                        Hud.instance.StaminaBarNoStaminaFlash();
                    return;
                }
                player.UseStamina(cost);
            }

            float range = GetRange(player);
            ApplyDetectionRange(EnemyHud.instance);
            int found = 0;

            foreach (Character character in Character.GetAllCharacters())
            {
                if (character == null || character == player || character.IsPlayer() || character.IsDead())
                    continue;

                if (Vector3.Distance(player.transform.position, character.transform.position) > range)
                    continue;

                found++;
                Reveal(character);
            }

            if (Plugin.ShowVisual.Value)
                PlayVisual(player, range);
            if (Plugin.PlayAudio.Value)
                PlaySound(player);
            if (Plugin.ShowMessage.Value)
                ShowResult(player, found);

            CleanupPins();
        }

        private static void Reveal(Character character)
        {
            EnemyHud hud = EnemyHud.instance;
            if (hud == null || ShowHudMethod == null)
                return;

            try
            {
                ShowHudMethod.Invoke(hud, new object[] { character, false });

                object dictionaryObject = HudsField?.GetValue(hud);
                if (dictionaryObject is IDictionary dictionary && dictionary.Contains(character))
                {
                    object hudData = dictionary[character];
                    FieldInfo hoverTimer = AccessTools.Field(hudData.GetType(), "m_hoverTimer");
                    FieldInfo guiField = AccessTools.Field(hudData.GetType(), "m_gui");
                    hoverTimer?.SetValue(hudData, 0f);
                    if (guiField?.GetValue(hudData) is GameObject gui)
                        gui.SetActive(true);
                }

                if (Plugin.ShowMinimapIcons.Value)
                    AddOrRefreshPin(character);
            }
            catch
            {
                // A single creature failing to reveal should not break the entire ping.
            }
        }

        private static float GetRevealDuration()
        {
            if (EnemyHud.instance == null || HoverShowDurationField == null)
                return 1f;
            object value = HoverShowDurationField.GetValue(EnemyHud.instance);
            return value is float duration ? Mathf.Max(0.25f, duration) : 1f;
        }

        private static void AddOrRefreshPin(Character character)
        {
            if (Minimap.instance == null)
                return;

            float expire = Time.time + GetRevealDuration();
            if (Pins.TryGetValue(character, out TrackedPin existing))
            {
                existing.ExpireAt = expire;
                if (existing.Pin != null)
                    existing.Pin.m_pos = character.GetCenterPoint();
                return;
            }

            Minimap.PinData pin = Minimap.instance.AddPin(character.GetCenterPoint(), Minimap.PinType.RandomEvent,
                "EnemySense", false, false);
            if (pin != null)
                Pins[character] = new TrackedPin { Character = character, Pin = pin, ExpireAt = expire };
        }

        internal static void UpdatePins()
        {
            if (Pins.Count == 0)
                return;

            List<Character> remove = null;
            foreach (KeyValuePair<Character, TrackedPin> pair in Pins)
            {
                Character character = pair.Key;
                TrackedPin tracked = pair.Value;
                if (character == null || character.IsDead() || Time.time >= tracked.ExpireAt || Minimap.instance == null)
                {
                    if (remove == null) remove = new List<Character>();
                    remove.Add(character);
                    continue;
                }

                if (tracked.Pin != null)
                    tracked.Pin.m_pos = character.GetCenterPoint();
            }

            if (remove == null)
                return;
            foreach (Character character in remove)
                RemovePin(character);
        }

        internal static void CleanupPins() => UpdatePins();

        private static void RemovePin(Character character)
        {
            if (!Pins.TryGetValue(character, out TrackedPin tracked))
                return;
            if (Minimap.instance != null && tracked.Pin != null)
                Minimap.instance.RemovePin(tracked.Pin);
            Pins.Remove(character);
        }

        internal static void RemoveAllPins()
        {
            if (Minimap.instance != null)
            {
                foreach (TrackedPin tracked in Pins.Values)
                    if (tracked.Pin != null)
                        Minimap.instance.RemovePin(tracked.Pin);
            }
            Pins.Clear();
        }

        private static void ShowResult(Player player, int found)
        {
            string text = string.IsNullOrEmpty(Plugin.CustomMessage.Value)
                ? $"{found} creature{(found == 1 ? "" : "s")} found nearby."
                : Plugin.CustomMessage.Value.Replace("#", found.ToString());

            player.Message(MessageHud.MessageType.Center,
                $"<color={Plugin.MessageColor.Value}>{text}</color>");
        }

        private static void PlayVisual(Player player, float range)
        {
            try
            {
                if (ZNetScene.instance == null)
                    return;
                GameObject prefab = ZNetScene.instance.GetPrefab("vfx_sledge_hit");
                if (prefab == null)
                    return;
                GameObject visual = UnityEngine.Object.Instantiate(prefab, player.GetHeadPoint(), Quaternion.identity);
                visual.transform.localScale = Vector3.one * Mathf.Max(1f, range / 30f);
            }
            catch { }
        }

        private static void PlaySound(Player player)
        {
            try
            {
                if (ZNetScene.instance == null)
                    return;
                GameObject prefab = ZNetScene.instance.GetPrefab("sfx_lootspawn");
                if (prefab != null)
                    UnityEngine.Object.Instantiate(prefab, player.GetHeadPoint(), Quaternion.identity);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.FixedUpdate))]
    internal static class PlayerFixedUpdatePatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || !__instance.TakeInput())
                return;

            bool pressed;
            KeyboardShortcut shortcut = Plugin.CustomKeybind.Value;
            if (shortcut.MainKey != KeyCode.None)
                pressed = shortcut.IsDown();
            else
                pressed = __instance.IsCrouching() && ZInput.GetButtonDown("ToggleWalk");

            if (pressed)
                Sonar.Ping(__instance);

            Sonar.UpdatePins();
        }
    }

    [HarmonyPatch(typeof(EnemyHud), "TestShow")]
    internal static class EnemyHudTestShowPatch
    {
        private static void Prefix(EnemyHud __instance)
        {
            Sonar.ApplyDetectionRange(__instance);
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.SetWalk))]
    internal static class CharacterSetWalkPatch
    {
        private static void Prefix(Character __instance, ref bool walk)
        {
            if (Plugin.CustomKeybind.Value.MainKey != KeyCode.None)
                return;
            if (__instance == Player.m_localPlayer && __instance.IsCrouching() && ZInput.GetButtonDown("ToggleWalk"))
                walk = __instance.GetWalk();
        }
    }

    [HarmonyPatch(typeof(Minimap), "UpdateMap")]
    internal static class MinimapUpdateMapPatch
    {
        private static void Prefix(Minimap __instance)
        {
            if (Plugin.AdditionalZoom.Value)
                __instance.m_minZoom = 0.00375f;
        }
    }
}
