using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

[assembly: AssemblyVersion("0.1.2.0")]
[assembly: AssemblyFileVersion("0.1.2.0")]

namespace SephiriaAutoParry
{
    [BepInPlugin("local.sephiria.autoparry", "Sephiria Auto Parry", "0.1.2")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.1.2";
        private static readonly GuardThreatSelection guardThreat = new GuardThreatSelection();
        private static int guardThreatFrame = -1;
        private static Vector2 requestedGuardDirection;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ForceCancel;
        internal static Plugin Instance;
        internal static bool SyntheticInput;
        internal static int LastDamageFrame = -1;
        internal static void PressSpecial(WeaponSimple weapon)
        {
            var controller = weapon.Networkowner;
            TryForceCancel(weapon);
            var ticket = ActionTicket.Arm(weapon);
            ticket.CaptureBeforeInput(controller);
            ticket.GuardDirection = weapon is WeaponSimple_SwordAndShield || weapon is WeaponSimple_QuartterStaff ?
                requestedGuardDirection : Vector2.zero;
            var originalAim = controller.aimedPositionClientside;
            if (ticket.GuardDirection.sqrMagnitude > 0f)
            {
                controller.aimedPositionClientside = controller.transform.position + (Vector3)(ticket.GuardDirection * 10f);
                controller.Aim(controller.aimedPositionClientside);
            }
            SyntheticInput = true;
            try
            {
                weapon.SubAttackButtonDown();
                // Observe only; the game's normal animator update decides when input starts.
                ticket.EvaluateStart();
            }
            finally { SyntheticInput = false; controller.aimedPositionClientside = originalAim; }
        }
        internal static void ReleaseSpecial(WeaponSimple weapon)
        {
            SyntheticInput = true;
            try { weapon.SubAttackButtonUp(); }
            finally { SyntheticInput = false; }
        }
        private Harmony harmony;
        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("Gameplay", "AutoParry", true,
                "Start native defensive specials before predicted impacts. Native MP costs and defense rules apply.");
            ForceCancel = Config.Bind("Gameplay", "ForceCancel", false,
                "Cancel current attacks/casts/dashes before automatic special input. Never interrupt katana sheath/draw state. Default off.");
            UpdateUI.CheckOnStartup = Config.Bind("Updates", "CheckOnStartup", true,
                "Check GitHub Releases on startup; ask before downloading; apply on next launch.");
            gameObject.AddComponent<UpdateUI>();
            harmony = new Harmony("local.sephiria.autoparry");
            try
            {
                harmony.PatchAll(typeof(Plugin).Assembly);
                Logger.LogInfo("Auto parry " + Version + " loaded; predictive native actions; enabled=" + Enabled.Value);
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Logger.LogError("Auto parry was not installed: " + e);
            }
        }
        private void Update()
        {
            if (!Enabled.Value || Time.timeScale <= 0f || !CombatManager.Instance) return;
            var player = Player;
            if (!player || !player.isServer || player.IsDead) return;
            var controller = player.GetComponent<WeaponControllerSimple>();
            if (!controller || ActionTicket.IsPending(controller.currentWeapon)) return;
            foreach (var enemy in CombatManager.Instance.AllCreatures)
            {
                if (!enemy || enemy == player || enemy.IsDead || !enemy.isServer ||
                    !CombatManager.ContainsAttackableFaction(enemy.GetHostileFactionLayers(EDamageFromType.DirectAttack), player.faction) ||
                    ((Vector2)(enemy.transform.position - player.transform.position)).sqrMagnitude > 400f) continue;
                var prediction = enemy.GetComponent<EnemyPreparationPrediction>();
                if (!prediction) prediction = enemy.gameObject.AddComponent<EnemyPreparationPrediction>();
                prediction.Check(player);
            }
        }
        internal static void Log(string message) { Instance.Logger.LogInfo(message); }
        internal static PlayerAvatar Player
        {
            get
            {
                var identity = Mirror.NetworkClient.localPlayer;
                return identity ? identity.GetComponent<PlayerAvatar>() : null;
            }
        }
        internal static float Lead { get { return 0.10f; } }
        private static readonly FieldInfo BladeStuck = AccessTools.Field(typeof(WeaponSimple_Katana), "isBladeStuck");
        private static readonly FieldInfo QuickDrawRunning = AccessTools.Field(typeof(WeaponSimple_Katana), "isQuickDrawAnimationRunning");
        private static readonly FieldInfo QuickDrawWaiting = AccessTools.Field(typeof(WeaponSimple_Katana), "isWaitQuickDrawAnimation");
        internal static bool Defend(string threat, float timeToImpact, Vector2 incomingDirection)
        {
            var player = Player;
            var controller = player ? player.GetComponent<WeaponControllerSimple>() : null;
            bool directional = controller && (controller.currentWeapon is WeaponSimple_SwordAndShield ||
                controller.currentWeapon is WeaponSimple_QuartterStaff);
            if (directional && timeToImpact > 0f && incomingDirection.sqrMagnitude > 0f)
            {
                if (!Enabled.Value || Time.frameCount == LastDamageFrame || player.IsDead || player.isGuardEnabled ||
                    ActionTicket.IsPending(controller.currentWeapon)) return false;
                if (guardThreatFrame != Time.frameCount) { guardThreat.Clear(); guardThreatFrame = Time.frameCount; }
                guardThreat.Offer(Time.time + timeToImpact, incomingDirection.x, incomingDirection.y, threat);
                // Detectors keep observing; selection is resolved after all Update callbacks.
                return false;
            }
            requestedGuardDirection = directional ? incomingDirection.normalized : Vector2.zero;
            try { return DefendNow(threat, timeToImpact); }
            finally { requestedGuardDirection = Vector2.zero; }
        }
        private void LateUpdate()
        {
            if (guardThreatFrame != Time.frameCount || !guardThreat.HasValue) return;
            requestedGuardDirection = new Vector2(guardThreat.X, guardThreat.Y);
            try
            {
                // Never replay a forecast after its predicted contact time.
                if (guardThreat.Deadline > Time.time && Time.frameCount != LastDamageFrame)
                    DefendNow(guardThreat.Threat, guardThreat.Deadline - Time.time);
            }
            finally { guardThreat.Clear(); requestedGuardDirection = Vector2.zero; }
        }
        private static bool DefendNow(string threat, float timeToImpact)
        {
            var unit = Player;
            if (Time.frameCount == LastDamageFrame || !Enabled.Value || !unit || !unit.isServer || !unit.isOwned || unit.IsDead ||
                unit.isGuardEnabled || unit.parryInvincibleApplied > 0 || unit.isCounterInvincibleApplied > 0)
                return false;
            var controller = unit.GetComponent<WeaponControllerSimple>();
            if (!controller || !controller.currentWeapon || ActionTicket.IsPending(controller.currentWeapon))
                return false;
            var weapon = controller.currentWeapon;
            if (controller.currentWeaponSwing >= 10) return false;
            var dagger = weapon as WeaponSimple_Dagger;
            var staff = weapon as WeaponSimple_QuartterStaff;
            var shield = weapon as WeaponSimple_SwordAndShield;
            var katana = weapon as WeaponSimple_Katana;
            string action;
            if (dagger)
            {
                if (dagger.parryReserved) return false;
                bool fury = dagger.currentFury > 0 && !dagger.basicAttackFinal;
                int cost = fury ? dagger.FuryCost : dagger.ParryCost;
                if ((!fury && (dagger.critFury || unit.GetCustomStatUnsafe("EVASIONFURY") > 0)) ||
                    unit.MP < cost) return false;
                // Exactly the weapon input path: available Fury takes priority over parry.
                PressSpecial(dagger);
                action = fury ? "Dagger Fury input" : "Dagger parry input";
            }
            else if (staff)
            {
                if (!unit.IsAvailableGuard || staff.overrideSpecialAttackAddon ||
                    !String.IsNullOrEmpty(staff.changedSpecialAttackParameter) || staff.enableBigThrowingSpear ||
                    unit.MP < staff.SpecialAttackCost || unit.MP <= 0) return false;
                PressSpecial(staff);
                action = "Staff parry";
            }
            else if (shield)
            {
                if (!unit.IsAvailableGuard || !shield.isGuardAvailable || unit.MP <= 0) return false;
                PressSpecial(shield);
                action = "Shield guard input";
            }
            else if (katana)
            {
                if (katana.attackMoveSet == 1 || (katana.useSheathHardening && (bool)BladeStuck.GetValue(katana)))
                    return false;
                // Let the real sheath/draw sequence finish its state-changing events.
                // Cancelling it and reconstructing only some flags can leave attacks locked.
                if (katana.isSheathAnimationRunning || (bool)QuickDrawRunning.GetValue(katana) ||
                    (bool)QuickDrawWaiting.GetValue(katana))
                    return false;
                var type = katana.sheathActionType;
                if (type != WeaponSimple_Katana.ESheathActionType.Sheath &&
                    type != WeaponSimple_Katana.ESheathActionType.Deflecting &&
                    type != WeaponSimple_Katana.ESheathActionType.CloudSlash) return false;
                if (type != WeaponSimple_Katana.ESheathActionType.Sheath && unit.MP < katana.SpecialAttackCost)
                    return false;
                if (type == WeaponSimple_Katana.ESheathActionType.Sheath)
                {
                    // Native sheath/draw protection lasts 0.125s. A 0.16s prediction
                    // can expire before contact; keep observing until closer to the impact.
                    if (timeToImpact > 0.075f) return false;
                    bool guard = controller.animator.GetBool(AnimHashContainer.Instance.GuardHash);
                    // Flags can briefly disagree until the next animation event. Do not toggle then.
                    if (katana.isBladeSheathed != guard || katana.sheathStateEnabled != guard) return false;
                    // A settled sheath is drawn only through the native special-key toggle.
                    PressSpecial(katana);
                    action = guard ? "Katana draw" : "Katana sheath";
                }
                else
                {
                    PressSpecial(katana);
                    action = "Katana " + type;
                }
            }
            else return false;
            var ticket = weapon.GetComponent<ActionTicket>();
            ticket.Controller = controller;
            ticket.ReleaseShield = shield != null;
            string trace = action + " queued BEFORE " + threat + "; predicted lead=" + timeToImpact.ToString("F3") +
                "s; MP before native animation=" + unit.MP;
            CombatTrace.Record(trace);
            return true;
        }
        private static void TryForceCancel(WeaponSimple weapon)
        {
            if (!ForceCancel.Value) return;
            var katana = weapon as WeaponSimple_Katana;
            if (katana && (katana.isBladeSheathed || katana.sheathStateEnabled || katana.isSheathAnimationRunning ||
                (bool)QuickDrawRunning.GetValue(katana) || (bool)QuickDrawWaiting.GetValue(katana) ||
                katana.Networkowner.animator.GetBool(AnimHashContainer.Instance.GuardHash) ||
                katana.Networkowner.animator.IsInTransition(0))) return;
            var unit = weapon.Networkowner.unitAvatar;
            if (unit.CurrentDashModule && unit.CurrentDashModule.IsDashing) unit.CurrentDashModule.StopDash();
            unit.CancelCurrentAction();
        }
    }

    public sealed class ActionTicket : MonoBehaviour
    {
        internal float Until, QueuedAt, PendingUntil;
        internal bool ReleaseShield, Started, Cancelled;
        internal Vector2 GuardDirection;
        private bool guardAimStarted;
        private int ownedTrigger;
        private bool guardBefore;
        private WeaponSimple requestedWeapon;

        internal WeaponControllerSimple Controller;
        internal bool TryGetGuardAim(out Vector3 point)
        {
            point = Vector3.zero;
            if (!Plugin.Enabled.Value || Cancelled || !Controller || Controller.currentWeapon != requestedWeapon ||
                !Controller.unitAvatar || Controller.unitAvatar.IsDead || GuardDirection.sqrMagnitude < 0.0001f)
                return false;
            float aimUntil = requestedWeapon is WeaponSimple_QuartterStaff ? QueuedAt + 3f : Until;
            if (Controller.unitAvatar.isGuardEnabled) guardAimStarted = true;
            if (guardAimStarted && !Controller.unitAvatar.isGuardEnabled) return false;
            if (Time.time >= aimUntil || (Started && !Controller.unitAvatar.isGuardEnabled &&
                Controller.currentWeaponSwing < 10) || (!Started && Time.time >= PendingUntil)) return false;
            point = Controller.transform.position + (Vector3)(GuardDirection * 10f);
            return true;
        }

        internal static bool IsPending(WeaponSimple weapon)
        {
            var ticket = weapon ? weapon.GetComponent<ActionTicket>() : null;
            return ticket && !ticket.Cancelled && Time.time < ticket.PendingUntil;
        }
        internal static bool IsRecent(WeaponSimple weapon)
        {
            var ticket = weapon ? weapon.GetComponent<ActionTicket>() : null;
            return ticket && !ticket.Cancelled && Time.time - ticket.QueuedAt < 0.65f;
        }
        internal static ActionTicket Arm(WeaponSimple weapon)
        {
            var ticket = weapon.GetComponent<ActionTicket>();
            if (!ticket) ticket = weapon.gameObject.AddComponent<ActionTicket>();
            ticket.Started = false;
            ticket.Cancelled = false;
            ticket.ReleaseShield = false;
            ticket.guardAimStarted = false;
            ticket.QueuedAt = Time.time;
            ticket.PendingUntil = Time.time + 0.12f;
            ticket.Until = Time.time + 0.45f;
            return ticket;
        }
        internal void CaptureBeforeInput(WeaponControllerSimple controller)
        {
            Controller = controller;
            requestedWeapon = controller.currentWeapon;
            ownedTrigger = 0;
            var dagger = controller.currentWeapon as WeaponSimple_Dagger;
            var katana = controller.currentWeapon as WeaponSimple_Katana;
            if (dagger) ownedTrigger = dagger.currentFury > 0 && !dagger.basicAttackFinal ?
                AnimHashContainer.Instance.DashAttackHash : AnimHashContainer.Instance.ParryHash;
            else if (controller.currentWeapon is WeaponSimple_QuartterStaff ||
                (katana && katana.sheathActionType != WeaponSimple_Katana.ESheathActionType.Sheath))
                ownedTrigger = AnimHashContainer.Instance.SweepHash;
            guardBefore = controller.animator.GetBool(AnimHashContainer.Instance.GuardHash);
        }
        internal void EvaluateStart()
        {
            if (Started || Cancelled || !Controller || !Controller.animator) return;
            if (Controller.currentWeapon != requestedWeapon) { Cancelled = true; return; }
            var katana = Controller.currentWeapon as WeaponSimple_Katana;
            // These flags are set by the actual animation events. A mere transition
            // back to idle after cancellation must not count as using the special.
            Started = Controller.currentWeaponSwing >= 10 || Controller.unitAvatar.isGuardEnabled ||
                (katana && katana.isSheathAnimationRunning);
        }        internal void CancelIfNotStarted()
        {
            if (Cancelled || Started) return;
            EvaluateStart();
            if (Started || Cancelled) return;
            // Discard only our outstanding automatic request, before damage can be applied.
            Cancelled = true;
            ReleaseShield = false;
            PendingUntil = 0f;
            // Withdraw only our unconsumed input, without cancelling the player's current action.
            if (ownedTrigger != 0) Controller.ResetAnimationTrigger(ownedTrigger);
            var dagger = Controller.currentWeapon as WeaponSimple_Dagger;
            if (dagger) dagger.parryReserved = false;
            var shield = Controller.currentWeapon as WeaponSimple_SwordAndShield;
            if (shield) Plugin.ReleaseSpecial(shield);
            var katana = Controller.currentWeapon as WeaponSimple_Katana;
            if (katana && katana.sheathActionType == WeaponSimple_Katana.ESheathActionType.Sheath &&
                Controller.animator.GetBool(AnimHashContainer.Instance.GuardHash) != guardBefore)
            {
                // Native toggle back while no sheath/draw event has started.
                Plugin.SyntheticInput = true;
                try { katana.SubAttackButtonDown(); }
                finally { Plugin.SyntheticInput = false; }
            }
            CombatTrace.Record("Unstarted automatic special cancelled; weapon=" + requestedWeapon.GetType().Name +
                "; elapsed=" + (Time.time - QueuedAt).ToString("F3"));
        }        private void Update()
        {
            if (!Started && !Cancelled && Time.time - QueuedAt < 0.65f)
                EvaluateStart();
            if (!Started && !Cancelled && Time.time >= PendingUntil) CancelIfNotStarted();
            if (!ReleaseShield || Time.time < Until) return;
            ReleaseShield = false;
            var shield = GetComponent<WeaponSimple_SwordAndShield>();
            if (shield && Controller && Controller.currentWeapon == shield)
                Plugin.ReleaseSpecial(shield);
        }
    }

    [HarmonyPatch(typeof(WeaponSimple_SwordAndShield), "SubAttackButtonDown")]
    internal static class ManualShieldInput
    {
        private static void Prefix(WeaponSimple_SwordAndShield __instance)
        {
            if (Plugin.SyntheticInput) return;
            var ticket = __instance.GetComponent<ActionTicket>();
            if (ticket) ticket.ReleaseShield = false;
        }
    }
    // Change the native visual/aim input, never the guard result or guard angle.
    [HarmonyPatch(typeof(WeaponControllerSimple), "Aim")]
    internal static class AutomaticGuardAim
    {
        private static void Prefix(WeaponControllerSimple __instance, ref Vector2 aimPoint)
        {
            if (!__instance.currentWeapon) return;
            var ticket = __instance.currentWeapon.GetComponent<ActionTicket>();
            Vector3 automaticAim;
            if (ticket && ticket.TryGetGuardAim(out automaticAim)) aimPoint = automaticAim;
        }
    }
    [HarmonyPatch]
    internal static class ManualSpecialInput
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(WeaponSimple_Dagger), typeof(WeaponSimple_QuartterStaff),
                typeof(WeaponSimple_SwordAndShield), typeof(WeaponSimple_Katana) })
                yield return AccessTools.Method(type, "SubAttackButtonDown");
        }
        private static void Prefix(WeaponSimple __instance)
        {
            if (Plugin.SyntheticInput) return;
            var ticket = __instance.GetComponent<ActionTicket>();
            if (ticket) { ticket.Cancelled = true; ticket.ReleaseShield = false; }
        }
    }
    // A pooled warning receives a fresh prediction each time the factory initializes it.
    // This never creates hit/block states or calls animation events ahead of the animator.
    public sealed class WarningPrediction : MonoBehaviour
    {
        internal Vector2 Center, Size;
        internal float Angle, Deadline, Radius;
        internal bool Circle, Forward, Fired;
        internal string Source;
        private void Update()
        {
            if (Fired || !Plugin.Enabled.Value || Time.timeScale <= 0f) return;
            float remaining = Deadline - Time.time;
            if (remaining <= 0f) { Fired = true; return; }
            if (remaining > Plugin.Lead) return;
            var player = Plugin.Player;
            if (!player) return;
            Vector2 delta = (Vector2)player.transform.position - Center;
            bool overlaps;
            if (Circle) overlaps = delta.sqrMagnitude <= (Radius + 0.25f) * (Radius + 0.25f);
            else
            {
                Vector2 local = Quaternion.Euler(0f, 0f, -Angle) * delta;
                // The forward line extends along its local Y axis; centered lines cover both sides.
                if (Forward) local.y -= Size.y * 0.5f;
                overlaps = Mathf.Abs(local.x) <= Size.x * 0.5f + 0.25f &&
                    Mathf.Abs(local.y) <= Size.y * 0.5f + 0.25f;
            }
            Vector2 incoming = Center - (Vector2)player.transform.position;
            if (Forward) incoming = -(Vector2)(Quaternion.Euler(0f, 0f, Angle) * Vector2.up);
            if (overlaps && Plugin.Defend(Source, remaining, incoming)) Fired = true;
        }
    }

    [HarmonyPatch]
    internal static class WarningPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(AOEWarningFactory).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "CreateAoe_MeleeAttackLine_Windmill" ||
                    m.Name == "CreateAoe_MeleeAttackLine_Center" || m.Name == "CreateAoe_MeleeAttackLine" ||
                    m.Name == "CreateAoe_Circle" || m.Name == "CreateAoe_Rectangle");
        }
        private static void Postfix(MethodBase __originalMethod, object[] __args, object __result)
        {
            var component = __result as Component;
            if (!component) return;
            var prediction = component.GetComponent<WarningPrediction>();
            if (prediction) prediction.Fired = true;
            var parameters = __originalMethod.GetParameters();
            Color color = Color.clear;
            Vector2 center = Vector2.zero, size = Vector2.zero;
            float duration = 0f, angle = 0f, radius = 0f;
            for (int i = 0; i < parameters.Length; i++)
            {
                switch (parameters[i].Name)
                {
                    case "color": color = (Color)__args[i]; break;
                    case "position": case "to": center = (Vector3)__args[i]; break;
                    case "size": size = (Vector2)__args[i]; break;
                    case "time": duration = (float)__args[i]; break;
                    case "angle": angle = (float)__args[i]; break;
                    case "radius": radius = (float)__args[i]; break;
                }
            }
            Color hostile = AOEWarningFactory.HostileWarningColor;
            if (Mathf.Abs(color.r - hostile.r) + Mathf.Abs(color.g - hostile.g) + Mathf.Abs(color.b - hostile.b) > 0.05f ||
                duration <= 0f || !Plugin.Enabled.Value) return;
            if (!prediction) prediction = component.gameObject.AddComponent<WarningPrediction>();
            prediction.Center = center;
            prediction.Size = size;
            prediction.Angle = angle;
            prediction.Radius = radius;
            prediction.Circle = __originalMethod.Name == "CreateAoe_Circle";
            prediction.Forward = __originalMethod.Name == "CreateAoe_MeleeAttackLine";
            prediction.Deadline = Time.time + duration;
            prediction.Source = __originalMethod.Name;
            prediction.Fired = false;
        }
    }

    public sealed class BulletPrediction : MonoBehaviour
    {
        private Vector2 previous;
        private float previousTime;
        private bool initialized, fired;
        private void OnEnable() { initialized = false; fired = false; }
        internal void Check(Bullet bullet)
        {
            Vector2 position = bullet.transform.position;
            float now = Time.time;
            Vector2 velocity = initialized && now > previousTime ? (position - previous) / (now - previousTime) : Vector2.zero;
            previous = position; previousTime = now; initialized = true;
            if (fired || !Plugin.Enabled.Value || !bullet.isServer || Time.timeScale <= 0f ||
                bullet.damageDealtType != Bullet.EDamageDealtType.Normal ||
                bullet.collosionType == Bullet.ECollisionTiming.None ||
                (bullet.DestroyModule && bullet.DestroyModule.IsDestroyed)) return;
            var player = Plugin.Player;
            if (!player || !CombatManager.ContainsAttackableFaction(bullet.AttackableFactionLayers, player.faction) ||
                (bullet.Owner == player && !bullet.canAttackOwner)) return;
            if (!bullet.isCollisionEnabled || velocity.sqrMagnitude < 0.01f || !bullet.attackingCollider) return;
            Bounds bulletBounds = bullet.attackingCollider.bounds;
            float impact = float.PositiveInfinity;
            if (bullet.collisionDemension == Bullet.ECollisionDemension.Ground)
            {
                impact = EntryTime((Vector2)player.transform.position - (Vector2)bulletBounds.center,
                    bulletBounds.extents, velocity);
            }
            else
            {
                var geometry = player.GetComponent<PlayerHitGeometry>();
                if (!geometry) geometry = player.gameObject.AddComponent<PlayerHitGeometry>();
                foreach (var hit in geometry.Colliders)
                {
                    if (!hit || !hit.enabled || !hit.gameObject.activeInHierarchy) continue;
                    var hitbox = hit.GetComponent<Hitbox>();
                    if (!hitbox || hitbox.GetCombatBehaviour(0) != player) continue;
                    Bounds targetBounds = hit.bounds;
                    impact = Mathf.Min(impact, EntryTime(
                        (Vector2)targetBounds.center - (Vector2)bulletBounds.center,
                        (Vector2)targetBounds.extents + (Vector2)bulletBounds.extents, velocity));
                }
            }
            Vector2 incoming = velocity.sqrMagnitude > 0.0001f ? -velocity : position - (Vector2)player.transform.position;
            if (impact >= 0f && impact <= 0.16f && Plugin.Defend("projectile hitbox", impact, incoming)) fired = true;
        }
        // Swept bounds use the same body-space coordinates as Bullet's native overlap.
        // This avoids predicting against the feet when the bullet hits the upper body.
        internal static float EntryTime(Vector2 delta, Vector2 extent, Vector2 velocity)
        {
            float entry = float.NegativeInfinity, exit = float.PositiveInfinity;
            for (int axis = 0; axis < 2; axis++)
            {
                if (Mathf.Abs(velocity[axis]) < 0.0001f)
                {
                    if (Mathf.Abs(delta[axis]) > extent[axis]) return float.PositiveInfinity;
                    continue;
                }
                float a = (delta[axis] - extent[axis]) / velocity[axis];
                float b = (delta[axis] + extent[axis]) / velocity[axis];
                entry = Mathf.Max(entry, Mathf.Min(a, b));
                exit = Mathf.Min(exit, Mathf.Max(a, b));
            }
            return entry <= exit && exit >= 0f ? Mathf.Max(0f, entry) : float.PositiveInfinity;
        }
    }
    public sealed class PlayerHitGeometry : MonoBehaviour
    {
        internal Collider2D[] Colliders;
        private void Awake() { Colliders = GetComponentsInChildren<Collider2D>(true); }
    }    [HarmonyPatch(typeof(Bullet), "Update")]
    internal static class BulletPatch
    {
        private static void Prefix(Bullet __instance)
        {
            if (!Plugin.Enabled.Value || !__instance.isServer) return;
            var prediction = __instance.GetComponent<BulletPrediction>();
            if (!prediction) prediction = __instance.gameObject.AddComponent<BulletPrediction>();
            prediction.Check(__instance);
            if (__instance.DestroyModule is BulletDestroyModule_Explode || __instance.DestroyModule is BulletDestroyModule_HeavyExplode)
            {
                var explosion = __instance.GetComponent<ExplosionPrediction>();
                if (!explosion) explosion = __instance.gameObject.AddComponent<ExplosionPrediction>();
                explosion.Check(__instance);
            }
        }
    }

    public sealed class ExplosionPrediction : MonoBehaviour
    {
        private bool fired;
        private Animator2D_Basic[] animators;
        private static readonly FieldInfo Frame = AccessTools.Field(typeof(Animator2D_Basic), "currentFrameIdx");
        private static readonly FieldInfo FrameTimer = AccessTools.Field(typeof(Animator2D_Basic), "nextFrameTimer");
        private static readonly FieldInfo State = AccessTools.Field(typeof(Animator2D_Basic), "currentState");
        private void Awake() { animators = GetComponentsInChildren<Animator2D_Basic>(true); }
        private void OnEnable() { fired = false; }
        internal void Check(Bullet bullet)
        {
            if (fired || !bullet.DestroyModule || bullet.DestroyModule.IsDestroyed) return;
            var player = Plugin.Player;
            if (!player || !CombatManager.ContainsAttackableFaction(bullet.AttackableFactionLayers, player.faction)) return;
            float lead = float.PositiveInfinity;
            var move = bullet.MoveModule;
            if (move && move.destroyOnTime && (!move.destroyOnGrounded ||
                (move.TopdownRigidbody && move.TopdownRigidbody.IsGrounded)))
                lead = move.destroyTimer.GetRemainingTime();
            // Some thrown dynamites burn down through animation events, not movement timers.
            foreach (var animator in animators)
            {
                if (!animator || !animator.isActiveAndEnabled) continue;
                var state = State.GetValue(animator) as AnimationSet.StateInfo;
                if (state == null || state.fps <= 0) continue;
                int frame = (int)Frame.GetValue(animator);
                var timer = (Timer)FrameTimer.GetValue(animator);
                foreach (var ev in state.frameEvents)
                {
                    if (ev.frame <= frame || !ev.events.Any(e =>
                        e.componentName == "Bullet" && e.methodName == "Destroy")) continue;
                    lead = Mathf.Min(lead, (ev.frame - frame) / (float)state.fps - timer.GetTimer());
                }
            }
            if (lead <= 0f || lead > 0.16f) return;
            Vector2 offset;
            float radius;
            bool ellipse;
            var normal = bullet.DestroyModule as BulletDestroyModule_Explode;
            var heavy = bullet.DestroyModule as BulletDestroyModule_HeavyExplode;
            if (normal)
            {
                offset = normal.explodeOffset;
                radius = normal.explodeRadius * normal.GetDestroyFxRangeBonus();
                ellipse = normal.explosionShape == BulletDestroyModule_Explode.EExplosionShape.Ellipse;
            }
            else if (heavy)
            {
                offset = heavy.explodeOffset;
                radius = heavy.explodeRadius;
                ellipse = heavy.explosionShape == BulletDestroyModule_HeavyExplode.EExplosionShape.Ellipse;
            }
            else return;
            Vector2 center = (Vector2)bullet.transform.position + offset;
            var geometry = player.GetComponent<PlayerHitGeometry>();
            if (!geometry) geometry = player.gameObject.AddComponent<PlayerHitGeometry>();
            foreach (var collider in geometry.Colliders)
            {
                if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                var hitbox = collider.GetComponent<Hitbox>();
                if (!hitbox || hitbox.GetCombatBehaviour(0) != player) continue;
                if ((collider.ClosestPoint(center) - center).sqrMagnitude > radius * radius) continue;
                if (ellipse)
                {
                    Vector2 delta = center - (Vector2)collider.transform.position;
                    delta.y *= 2f;
                    if (delta.sqrMagnitude > radius * radius) continue;
                }
                if (Plugin.Defend("delayed explosion", lead, center - (Vector2)player.transform.position)) fired = true;
                return;
            }
        }
    }
    // Classify real attack-producing callbacks instead of maintaining a monster-name list.
    internal static class AttackCallback
    {
        private static readonly Dictionary<MethodInfo, bool> Cache = new Dictionary<MethodInfo, bool>();
        internal static bool IsAttack(MethodInfo method)
        {
            if (method == null) return false;
            bool result;
            if (Cache.TryGetValue(method, out result)) return result;
            try { result = ReachesAttack(method, new HashSet<MethodBase>(), 0); }
            catch { result = false; }
            Cache[method] = result;
            return result;
        }
        private static bool ReachesAttack(MethodBase method, HashSet<MethodBase> visited, int depth)
        {
            if (method == null || !visited.Add(method) || depth > 6) return false;
            Type type = method.DeclaringType;
            if (type == null) return false;
            if (typeof(NewWeaponFireData).IsAssignableFrom(type) && method.Name.StartsWith("CreateAttack")) return true;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ProjectileSpawner<>) &&
                method.Name == "Spawn") return true;
            if (typeof(CombatBehaviour).IsAssignableFrom(type) && method.Name == "ApplyDamage") return true;
            if (type.Assembly != typeof(UnitAvatar).Assembly || method.GetMethodBody() == null) return false;
            foreach (var attribute in method.GetCustomAttributesData())
            {
                if (attribute.AttributeType.Name != "IteratorStateMachineAttribute" || attribute.ConstructorArguments.Count != 1) continue;
                var iterator = attribute.ConstructorArguments[0].Value as Type;
                if (iterator != null && ReachesAttack(AccessTools.Method(iterator, "MoveNext"), visited, depth + 1)) return true;
            }
            foreach (var instruction in PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null))
            {
                if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) continue;
                if (ReachesAttack(instruction.operand as MethodBase, visited, depth + 1)) return true;
            }
            return false;
        }
        internal static bool IsAttack(Component performer, string componentName, string methodName)
        {
            if (!performer || String.IsNullOrEmpty(methodName)) return false;
            var target = performer.GetComponent(componentName);
            return target && IsAttack(AccessTools.Method(target.GetType(), methodName));
        }
    }

    public sealed class EnemyPreparationPrediction : MonoBehaviour
    {
        private UnitAvatar enemy;
        private Animator2D_Basic[] spriteAnimators;
        private Animator[] unityAnimators;
        private readonly Dictionary<AnimationClip, AnimationEvent[]> clipEvents = new Dictionary<AnimationClip, AnimationEvent[]>();
        private float range = 3f;
        private static readonly FieldInfo State = AccessTools.Field(typeof(Animator2D_Basic), "currentState");
        private static readonly FieldInfo Frame = AccessTools.Field(typeof(Animator2D_Basic), "currentFrameIdx");
        private static readonly FieldInfo FrameTimer = AccessTools.Field(typeof(Animator2D_Basic), "nextFrameTimer");
        private static readonly FieldInfo Speed = AccessTools.Field(typeof(Animator2D_Basic), "currentSpeedMultiplier");
        private static readonly FieldInfo Events = AccessTools.Field(typeof(Animator2D_Basic), "events");
        private void Awake()
        {
            enemy = GetComponent<UnitAvatar>();
            spriteAnimators = GetComponentsInChildren<Animator2D_Basic>(true);
            unityAnimators = GetComponentsInChildren<Animator>(true);
            // Use this enemy's declared attack data for the nearby startup detector.
            for (Type type = enemy.GetType(); type != null && type != typeof(UnitAvatar); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(NewWeaponFireData).IsAssignableFrom(field.FieldType)) continue;
                    var data = field.GetValue(enemy) as NewWeaponFireData;
                    if (data) range = Mathf.Max(range, data.GetProjectileSize(0f).magnitude * 0.5f + 1f);
                }
            }
            range = Mathf.Min(range, 20f);
        }
        internal void Check(PlayerAvatar player)
        {
            if (((Vector2)(player.transform.position - enemy.transform.position)).sqrMagnitude > range * range) return;
            foreach (var animator in spriteAnimators)
            {
                if (!animator || !animator.isActiveAndEnabled || !animator.eventPerformer) continue;
                var state = State.GetValue(animator) as AnimationSet.StateInfo;
                if (state == null || state.fps <= 0) continue;
                int frame = (int)Frame.GetValue(animator);
                var timer = (Timer)FrameTimer.GetValue(animator);
                var multiplier = Speed.GetValue(animator) as Animator2D_Basic.GetSpeedDelegate;
                float speed = multiplier == null ? 1f : multiplier();
                if (speed <= 0f) continue;
                foreach (var ev in state.frameEvents)
                {
                    float lead = ((ev.frame - frame) / (float)state.fps - timer.GetTimer()) / speed;
                    if (ev.frame <= frame || lead <= 0f || lead > 0.16f) continue;
                    if (ev.events.Any(e => AttackCallback.IsAttack(animator.eventPerformer.transform, e.componentName, e.methodName)) &&
                        Plugin.Defend(enemy.GetType().Name + " attack preparation", lead, enemy.transform.position - player.transform.position)) return;
                }
                var registered = Events.GetValue(animator) as Dictionary<AnimationSet.StateInfo, Dictionary<int, Action>>;
                Dictionary<int, Action> callbacks;
                if (registered == null || !registered.TryGetValue(state, out callbacks)) continue;
                foreach (var ev in callbacks)
                {
                    float lead = ((ev.Key - frame) / (float)state.fps - timer.GetTimer()) / speed;
                    if (ev.Key <= frame || lead <= 0f || lead > 0.16f || ev.Value == null) continue;
                    if (ev.Value.GetInvocationList().Any(d => AttackCallback.IsAttack(d.Method)) &&
                        Plugin.Defend(enemy.GetType().Name + " registered attack preparation", lead, enemy.transform.position - player.transform.position)) return;
                }
            }
            foreach (var animator in unityAnimators)
            {
                if (!animator || !animator.isActiveAndEnabled || !animator.runtimeAnimatorController) continue;
                for (int layer = 0; layer < animator.layerCount; layer++)
                {
                    var state = animator.GetCurrentAnimatorStateInfo(layer);
                    float speed = animator.speed * state.speed * state.speedMultiplier;
                    if (speed <= 0f) continue;
                    foreach (var info in animator.GetCurrentAnimatorClipInfo(layer))
                    {
                        if (!info.clip || info.weight < 0.5f) continue;
                        AnimationEvent[] events;
                        if (!clipEvents.TryGetValue(info.clip, out events))
                        {
                            events = info.clip.events;
                            clipEvents[info.clip] = events;
                        }
                        float progress = state.loop ? state.normalizedTime % 1f : state.normalizedTime;
                        foreach (var ev in events)
                        {
                            float lead = (ev.time - progress * info.clip.length) / speed;
                            if (lead <= 0f || lead > 0.16f) continue;
                            foreach (var component in animator.GetComponents<MonoBehaviour>())
                            {
                                if (component && AttackCallback.IsAttack(AccessTools.Method(component.GetType(), ev.functionName)) &&
                                    Plugin.Defend(enemy.GetType().Name + " animation attack preparation", lead, enemy.transform.position - player.transform.position)) return;
                            }
                        }
                    }
                }
            }
        }
    }
    // Pre-spawn protection for the training launcher / FireTrap family.
    // Read the real animation's fire event; never delay or suppress the projectile.
    [HarmonyPatch(typeof(FireTrap), "Update")]
    internal static class LauncherPrediction
    {
        private static readonly FieldInfo Frame = AccessTools.Field(typeof(Animator2D_Basic), "currentFrameIdx");
        private static readonly FieldInfo FrameTimer = AccessTools.Field(typeof(Animator2D_Basic), "nextFrameTimer");
        private static void Prefix(FireTrap __instance)
        {
            if (!Plugin.Enabled.Value || !__instance.isServer || Time.timeScale <= 0f ||
                !CombatManager.Instance || CombatManager.Instance.PeaceMode) return;
            var player = Plugin.Player;
            if (!player) return;
            Vector2 origin = __instance.transform.position + __instance.firePoint;
            Vector2 direction = ((Vector2)__instance.fireDirection).normalized;
            Vector2 delta = (Vector2)player.transform.position - origin;
            float along = Vector2.Dot(delta, direction);
            if (along < -0.4f || along > 1.25f || Mathf.Abs(delta.x * direction.y - delta.y * direction.x) > 0.75f)
                return;
            var actor = __instance.GetComponent<TopdownActorRenderingMetadata>();
            var animator = actor ? actor.animator : null;
            if (!animator || !animator.currentSet) return;
            var state = animator.currentSet.sprites.FirstOrDefault(s => s.state == animator.CurrentStateName);
            if (state == null || state.fps <= 0) return;
            int frame = (int)Frame.GetValue(animator);
            var timer = (Timer)FrameTimer.GetValue(animator);
            foreach (var frameEvent in state.frameEvents)
            {
                if (frameEvent.frame <= frame ||
                    !frameEvent.events.Any(e => e.methodName == "FireBulletAnimation")) continue;
                float untilFire = (frameEvent.frame - frame) / (float)state.fps - timer.GetTimer();
                if (untilFire > 0f && untilFire <= 0.16f)
                    Plugin.Defend("launcher BEFORE projectile spawn", untilFire, -direction);
                break;
            }
        }
    }

    [HarmonyPatch(typeof(WeaponControllerSimple), "StartFuryInvincible")]
    internal static class FuryWindowObservation
    {
        private static void Postfix(WeaponControllerSimple __instance, float time)
        {
            if (__instance.unitAvatar != Plugin.Player ||
                !ActionTicket.IsRecent(__instance.currentWeapon)) return;
            string trace = "Native Fury/counter window opened; elapsed=" +
                (Time.time - __instance.currentWeapon.GetComponent<ActionTicket>().QueuedAt).ToString("F3") +
                "; duration=" + time.ToString("F3");
            CombatTrace.Record(trace);
        }
    }
    [HarmonyPatch(typeof(WeaponControllerSimple), "StartParryInvincible")]
    internal static class ParryWindowObservation
    {
        private static void Postfix(WeaponControllerSimple __instance, float time)
        {
            if (__instance.unitAvatar != Plugin.Player ||
                !ActionTicket.IsRecent(__instance.currentWeapon)) return;
            string trace = "Native parry window opened; elapsed=" +
                (Time.time - __instance.currentWeapon.GetComponent<ActionTicket>().QueuedAt).ToString("F3") +
                "; duration=" + time.ToString("F3");
            CombatTrace.Record(trace);
        }
    }
    // Last-chance input for every external hit source, including traps and explosions.
    // This does not postpone damage, alter the return value, or grant invincibility.
    [HarmonyPatch(typeof(UnitAvatar), "ApplyDamage")]
    internal static class IncomingAttackInput
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(UnitAvatar __instance, DamageInstance damage)
        {
            if (!Plugin.Enabled.Value || __instance != Plugin.Player || !__instance.isServer ||
                damage == null || damage.isSystemDamage || damage.damage <= 0f ||
                __instance.IsDead || __instance.IsInvulnerable)
                return;
            if (damage.origin is UnitAvatar &&
                !CombatManager.ContainsAttackableFaction(damage.targetFactionLayers, __instance.faction))
                return;
            Vector2 incoming = -damage.direction;
            if (incoming.sqrMagnitude < 0.0001f && damage.origin)
                incoming = damage.origin.transform.position - __instance.transform.position;
            Plugin.Defend("incoming hit input (" + damage.fromType + "/" + damage.damageType + ")", 0f, incoming);
            var controller = __instance.GetComponent<WeaponControllerSimple>();
            var ticket = controller && controller.currentWeapon ?
                controller.currentWeapon.GetComponent<ActionTicket>() : null;
            if (ticket && ActionTicket.IsRecent(controller.currentWeapon))
                ticket.CancelIfNotStarted();
        }
        private static void Finalizer(UnitAvatar __instance, DamageInstance damage)
        {
            if (__instance == Plugin.Player && damage != null && !damage.isSystemDamage && damage.damage > 0f)
                Plugin.LastDamageFrame = Time.frameCount;
        }    }
    [HarmonyPatch(typeof(UI_OptionsPanel), "OnOpened")]
    internal static class OptionsPatch
    {
        private static void Postfix(UI_OptionsPanel __instance)
        {
            // QoL creates its own copy of the options panel; use only the original.
            if (__instance.name.StartsWith("SephiriaQoL_")) return;
            try
            {
                var tab = __instance.tab.tabContents[0];
                var existing = tab.GetComponentInChildren<AutoParryOption>(true);
                if (existing) { existing.Refresh(); return; }
                var template = tab.GetComponentsInChildren<UI_OptionBox_Common_Integer>(true)
                    .FirstOrDefault(t => t.box && t.valueText);
                if (!template) throw new InvalidOperationException("Gameplay option template missing");
                var castOption = tab.GetComponentInChildren<UI_CastModeTypeBox>(true);
                if (!castOption || !castOption.box)
                    throw new InvalidOperationException("Casting input option missing");

                var staging = new GameObject("AutoParry_Staging");
                staging.SetActive(false);
                var row = UnityEngine.Object.Instantiate(template.gameObject, staging.transform);
                row.name = "SephiriaAutoParry_Option";
                row.SetActive(false);
                var original = row.GetComponent<UI_OptionBox_Common_Integer>();
                original.enabled = false;
                var box = original.box;
                var valueText = original.valueText.text;
                foreach (var localized in row.GetComponentsInChildren<UI_LocalizationStringText>(true))
                {
                    localized.enabled = false;
                    if (localized.text != valueText) localized.text.text = "자동 패링 / 가드";
                }
                UnityEngine.Object.Destroy(original);
                box.numberOfElements = 2;
                box.ValueChangedCallback = new UnityEngine.Events.UnityEvent<int>();
                box.forceNavUp = castOption.box;
                box.forceNavDown = castOption.box.forceNavDown;
                var navigation = box.navigation;
                navigation.mode = Navigation.Mode.Automatic;
                box.navigation = navigation;
                var option = row.AddComponent<AutoParryOption>();
                option.Box = box;
                option.ValueText = valueText;
                row.transform.SetParent(castOption.transform.parent, false);
                row.transform.SetSiblingIndex(castOption.transform.GetSiblingIndex() + 1);
                castOption.box.forceNavDown = box;
                if (box.forceNavDown is UI_HorizontalSelectionBox)
                    ((UI_HorizontalSelectionBox)box.forceNavDown).forceNavUp = box;
                row.SetActive(true);
                var cancel = AddUpdateRow(template, row.transform, box, "자동 패링 강제 캔슬", 3, staging.transform);
                var startup = AddUpdateRow(template, cancel.transform, cancel.Box, "시작할 때 자동 패링 업데이트 확인", 1, staging.transform);
                AddUpdateRow(template, startup.transform, startup.Box, "자동 패링 업데이트", 2, staging.transform);
                UnityEngine.Object.Destroy(staging);
                Plugin.Log("Added auto parry option to Gameplay tab");
            }
            catch (Exception e) { Plugin.Log("Could not add Gameplay option: " + e); }
        }
        private static AutoParryOption AddUpdateRow(UI_OptionBox_Common_Integer template, Transform after,
            UI_HorizontalSelectionBox previous, string label, int role, Transform staging)
        {
            var row = UnityEngine.Object.Instantiate(template.gameObject, staging);
            row.SetActive(false);
            row.name = "SephiriaAutoParry_Update_" + role;
            var original = row.GetComponent<UI_OptionBox_Common_Integer>();
            original.enabled = false;
            var box = original.box;
            var value = original.valueText.text;
            foreach (var localized in row.GetComponentsInChildren<UI_LocalizationStringText>(true))
            {
                localized.enabled = false;
                if (localized.text != value) localized.text.text = label;
            }
            UnityEngine.Object.Destroy(original);
            box.numberOfElements = 2;
            box.ValueChangedCallback = new UnityEngine.Events.UnityEvent<int>();
            box.forceNavUp = previous;
            box.forceNavDown = previous.forceNavDown;
            previous.forceNavDown = box;
            if (box.forceNavDown is UI_HorizontalSelectionBox)
                ((UI_HorizontalSelectionBox)box.forceNavDown).forceNavUp = box;
            var option = row.AddComponent<AutoParryOption>();
            option.Box = box;
            option.ValueText = value;
            option.Role = role;
            row.transform.SetParent(after.parent, false);
            row.transform.SetSiblingIndex(after.GetSiblingIndex() + 1);
            row.SetActive(true);
            return option;
        }
    }

    public sealed class AutoParryOption : MonoBehaviour
    {
        public int Role;
        public UI_HorizontalSelectionBox Box;
        public TMPro.TextMeshProUGUI ValueText;
        private void OnEnable() { Box.OnValueChanged += Changed; Refresh(); }
        private void OnDisable() { if (Box) Box.OnValueChanged -= Changed; }
        public void Refresh()
        {
            bool enabled = Role == 0 ? Plugin.Enabled.Value : (Role == 3 ? Plugin.ForceCancel.Value : UpdateUI.CheckOnStartup.Value);
            Box.ChangeValueWithoutNotify(Role == 2 ? 0 : (enabled ? 1 : 0));
            ValueText.text = Role == 2 ? "지금 확인" : (enabled ? "켜짐" : "꺼짐");
        }
        private void Changed(int value)
        {
            if (Role == 0) Plugin.Enabled.Value = value != 0;
            else if (Role == 3) Plugin.ForceCancel.Value = value != 0;
            else if (Role == 1) UpdateUI.CheckOnStartup.Value = value != 0;
            else if (UpdateUI.Instance) UpdateUI.Instance.Check(true);
            Refresh();
            if (Role == 0) Plugin.Log("Gameplay auto parry=" + Plugin.Enabled.Value);
        }
    }
}














