using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SephiriaAutoParry
{
    internal static class CombatTrace
    {
        private static readonly Queue<string> Recent = new Queue<string>();
        private static float nextWrite;
        private static bool writeErrorReported;
        internal static void Record(string message)
        {
            if (Recent.Count >= 50) Recent.Dequeue();
            Recent.Enqueue("t=" + Time.time.ToString("F3") + " frame=" + Time.frameCount + " " + message);
        }
        internal static void DumpDeath()
        {
            Save(true);
            Plugin.Log("DEATH TRACE BEGIN (last " + Recent.Count + " combat events)");
            foreach (var line in Recent) Plugin.Log(line);
            Plugin.Log("DEATH TRACE END");
            Recent.Clear();
        }
        internal static void Save(bool force)
        {
            if (!force && Time.unscaledTime < nextWrite) return;
            nextWrite = Time.unscaledTime + 1f;
            try
            {
                string directory = Path.Combine(Paths.CachePath, "SephiriaAutoParry");
                Directory.CreateDirectory(directory);
                // Overwrite the snapshot: keep the last 50, never the first 50.
                File.WriteAllLines(Path.Combine(directory, "combat-latest.log"), Recent.ToArray());
            }
            catch (Exception error)
            {
                if (!writeErrorReported) { Plugin.Log("Combat trace write failed: " + error.Message); writeErrorReported = true; }
            }
        }
        internal static string Origin(CombatBehaviour origin)
        {
            if (!origin) return "none";
            string text = origin.GetType().Name + "/" + origin.name;
            foreach (var field in origin.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (field.FieldType.IsEnum && (field.Name.IndexOf("state", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    field.Name.IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0))
                    text += " " + field.Name + "=" + field.GetValue(origin);
            return text;
        }
    }

    // Observes every local hit, including attacks for which no automatic request was made.
    [HarmonyPatch(typeof(UnitAvatar), "ApplyDamage")]
    internal static class CombatHitTrace
    {
        internal sealed class Snapshot
        {
            internal float Hp;
            internal string Detail;
        }
        private static void Prefix(UnitAvatar __instance, DamageInstance damage, out Snapshot __state)
        {
            __state = null;
            if (__instance != Plugin.Player || damage == null || damage.damage <= 0f) return;
            var controller = __instance.GetComponent<WeaponControllerSimple>();
            var weapon = controller ? controller.currentWeapon : null;
            var ticket = weapon ? weapon.GetComponent<ActionTicket>() : null;
            __state = new Snapshot { Hp = __instance.hp, Detail =
                "origin=" + CombatTrace.Origin(damage.origin) +
                "; object=" + (damage.damageObject ? damage.damageObject.name : "none") +
                "; id=" + damage.id + "; kind=" + damage.fromType + "/" + damage.damageType +
                "; raw=" + damage.damage.ToString("F2") + "; system=" + damage.isSystemDamage +
                "; direction=" + damage.direction + "; weapon=" + (weapon ? weapon.GetType().Name : "none") +
                "; MP=" + __instance.MP + "; enabled=" + Plugin.Enabled.Value + "; forceCancel=" + Plugin.ForceCancel.Value +
                "; guard=" + __instance.isGuardEnabled + "; parry=" + __instance.parryInvincibleApplied +
                "; counter=" + __instance.isCounterInvincibleApplied +
                "; swing=" + (controller ? controller.currentWeaponSwing : -999) +
                "; pending=" + (weapon && ActionTicket.IsPending(weapon)) +
                "; started=" + (ticket && ticket.Started) + "; cancelled=" + (ticket && ticket.Cancelled) };
        }
        private static void Postfix(UnitAvatar __instance, EApplyDamageResult __result, Snapshot __state)
        {
            if (__state == null) return;
            string line = "HIT result=" + __result + "; HP=" + __state.Hp.ToString("F2") + "->" +
                __instance.hp.ToString("F2") + "; " + __state.Detail;
            CombatTrace.Record(line);
            if (__instance.hp < __state.Hp) CombatTrace.Save(false);
            if (__state.Hp > 0f && (__instance.hp <= 0f || __instance.IsDead)) CombatTrace.DumpDeath();
        }
    }
}
