using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        public const string PluginVersion = "2.0.2";

        internal static ConfigEntry<float> DetectionRadius;
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
            DetectionRadius = Config.Bind(
                "Adjustments",
                "Detection Radius",
                60f,
                new ConfigDescription(
                    "Exact sonar detection radius in meters. Sneak skill no longer changes this value.",
                    new AcceptableValueRange<float>(1f, 500f)));

            StaminaDrain = Config.Bind("Adjustments", "Stamina Drain", 70f,
                "Stamina consumed by a sonar ping. Set to 0 for no stamina cost.");
            ShowMessage = Config.Bind("Features", "Show Message", true,
                "Show how many creatures were detected.");
            ShowVisual = Config.Bind("Features", "Visual Effect", true,
                "Play the sonar visual effect.");
            PlayAudio = Config.Bind("Features", "Audio Effect", true,
                "Play the sonar sound effect.");
            ShowMinimapIcons = Config.Bind("Features", "Minimap Icons", true,
                "Show detected creatures on the minimap while their health bar is revealed.");
            AdditionalZoom = Config.Bind("Features", "Additional Zoom", true,
                "Allows two additional zoom steps on the minimap.");
            CustomKeybind = Config.Bind("Features", "Custom Keybind", new KeyboardShortcut(KeyCode.None),
                "Optional dedicated sonar key. Leave as None to use crouch + Walk.");
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

    internal static class ReflectionCompat
    {
        internal static object DefaultArgument(ParameterInfo parameter)
        {
            if (parameter.HasDefaultValue && parameter.DefaultValue != DBNull.Value)
                return parameter.DefaultValue;

            Type type = parameter.ParameterType;
            if (type.IsByRef)
                type = type.GetElementType();

            return type != null && type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        internal static MethodInfo FindMethodWithPrefix(Type type, string name, params Type[] leadingTypes)
        {
            return type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == name)
                .Select(method => new { Method = method, Params = method.GetParameters() })
                .Where(x => x.Params.Length >= leadingTypes.Length)
                .Where(x =>
                {
                    for (int i = 0; i < leadingTypes.Length; i++)
                    {
                        if (x.Params[i].ParameterType != leadingTypes[i])
                            return false;
                    }
                    return true;
                })
                .OrderBy(x => x.Params.Length)
                .Select(x => x.Method)
                .FirstOrDefault();
        }

        internal static object InvokeWithLeadingArguments(object instance, MethodInfo method, params object[] leadingArguments)
        {
            if (instance == null || method == null)
                return null;

            ParameterInfo[] parameters = method.GetParameters();
            object[] args = new object[parameters.Length];

            int supplied = Math.Min(leadingArguments.Length, args.Length);
            for (int i = 0; i < supplied; i++)
                args[i] = leadingArguments[i];
            for (int i = supplied; i < args.Length; i++)
                args[i] = DefaultArgument(parameters[i]);

            return method.Invoke(instance, args);
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

        private static readonly MethodInfo AddPinMethod = ReflectionCompat.FindMethodWithPrefix(
            typeof(Minimap),
            "AddPin",
            typeof(Vector3), typeof(Minimap.PinType), typeof(string), typeof(bool), typeof(bool));

        private static readonly MethodInfo RemovePinMethod = ReflectionCompat.FindMethodWithPrefix(
            typeof(Minimap),
            "RemovePin",
            typeof(Minimap.PinData));

        private static readonly MethodInfo MessageMethod = ReflectionCompat.FindMethodWithPrefix(
            typeof(Player),
            "Message",
            typeof(MessageHud.MessageType), typeof(string));

        internal static float GetRange()
        {
            return Mathf.Max(1f, Plugin.DetectionRadius.Value);
        }

        internal static void ApplyDetectionRange(EnemyHud hud)
        {
            if (hud == null || MaxShowDistanceField == null)
                return;

            try
            {
                MaxShowDistanceField.SetValue(hud, GetRange());
            }
            catch
            {
                // A HUD field access failure must never break gameplay.
            }
        }

        internal static void Ping(Player player)
        {
            if (player == null || EnemyHud.instance == null)
                return;

            float cost = Mathf.Max(0f, Plugin.StaminaDrain.Value);
            if (cost > 0f)
            {
                if (!player.HaveStamina(cost))
                    return;
                player.UseStamina(cost);
            }

            float range = GetRange();
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

            UpdatePins();
        }

        private static void Reveal(Character character)
        {
            EnemyHud hud = EnemyHud.instance;
            if (hud == null || ShowHudMethod == null)
                return;

            try
            {
                ReflectionCompat.InvokeWithLeadingArguments(hud, ShowHudMethod, character);

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
                // One creature failing to reveal should not cancel the ping.
            }
        }

        private static float GetRevealDuration()
        {
            try
            {
                if (EnemyHud.instance == null || HoverShowDurationField == null)
                    return 1f;
                object value = HoverShowDurationField.GetValue(EnemyHud.instance);
                return value is float duration ? Mathf.Max(0.25f, duration) : 1f;
            }
            catch
            {
                return 1f;
            }
        }

        private static void AddOrRefreshPin(Character character)
        {
            if (Minimap.instance == null || AddPinMethod == null)
                return;

            float expire = Time.time + GetRevealDuration();
            if (Pins.TryGetValue(character, out TrackedPin existing))
            {
                existing.ExpireAt = expire;
                if (existing.Pin != null)
                    existing.Pin.m_pos = character.GetCenterPoint();
                return;
            }

            try
            {
                Minimap.PinData pin = ReflectionCompat.InvokeWithLeadingArguments(
                    Minimap.instance,
                    AddPinMethod,
                    character.GetCenterPoint(), Minimap.PinType.RandomEvent, "EnemySense", false, false) as Minimap.PinData;

                if (pin != null)
                    Pins[character] = new TrackedPin { Character = character, Pin = pin, ExpireAt = expire };
            }
            catch
            {
                // Minimap API changes should never break sonar.
            }
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
                    if (remove == null)
                        remove = new List<Character>();
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

        private static void RemovePin(Character character)
        {
            if (!Pins.TryGetValue(character, out TrackedPin tracked))
                return;

            try
            {
                if (Minimap.instance != null && tracked.Pin != null && RemovePinMethod != null)
                    ReflectionCompat.InvokeWithLeadingArguments(Minimap.instance, RemovePinMethod, tracked.Pin);
            }
            catch
            {
                // Ignore stale minimap objects during world unload.
            }

            Pins.Remove(character);
        }

        internal static void RemoveAllPins()
        {
            try
            {
                if (Minimap.instance != null && RemovePinMethod != null)
                {
                    foreach (TrackedPin tracked in Pins.Values)
                    {
                        if (tracked.Pin != null)
                            ReflectionCompat.InvokeWithLeadingArguments(Minimap.instance, RemovePinMethod, tracked.Pin);
                    }
                }
            }
            catch
            {
                // World may already be unloading.
            }

            Pins.Clear();
        }

        private static void ShowResult(Player player, int found)
        {
            string text = string.IsNullOrEmpty(Plugin.CustomMessage.Value)
                ? $"{found} creature{(found == 1 ? "" : "s")} found nearby."
                : Plugin.CustomMessage.Value.Replace("#", found.ToString());

            string formatted = $"<color={Plugin.MessageColor.Value}>{text}</color>";

            try
            {
                ReflectionCompat.InvokeWithLeadingArguments(
                    player,
                    MessageMethod,
                    MessageHud.MessageType.Center,
                    formatted);
            }
            catch
            {
                // A changed message signature must never break the sonar ping.
            }
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
            catch
            {
            }
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
            catch
            {
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.FixedUpdate))]
    internal static class PlayerFixedUpdatePatch
    {
        private static readonly MethodInfo TakeInputMethod = AccessTools.Method(typeof(Player), "TakeInput");

        private static bool CanTakeInput(Player player)
        {
            if (player == null)
                return false;

            try
            {
                if (TakeInputMethod == null)
                    return true;

                object result = TakeInputMethod.Invoke(player, null);
                return result is bool canTakeInput && canTakeInput;
            }
            catch
            {
                return true;
            }
        }

        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer)
                return;

            Sonar.UpdatePins();

            if (!CanTakeInput(__instance))
                return;

            KeyboardShortcut shortcut = Plugin.CustomKeybind.Value;
            bool pressed = shortcut.MainKey != KeyCode.None
                ? shortcut.IsDown()
                : __instance.IsCrouching() && ZInput.GetButtonDown("ToggleWalk");

            if (pressed)
                Sonar.Ping(__instance);
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
                walk = !walk;
        }
    }

    [HarmonyPatch(typeof(Minimap), "UpdateMap")]
    internal static class MinimapUpdateMapPatch
    {
        private static readonly FieldInfo MinZoomField = AccessTools.Field(typeof(Minimap), "m_minZoom");

        private static void Prefix(Minimap __instance)
        {
            if (!Plugin.AdditionalZoom.Value || MinZoomField == null)
                return;

            try
            {
                MinZoomField.SetValue(__instance, 0.00375f);
            }
            catch
            {
            }
        }
    }
}
