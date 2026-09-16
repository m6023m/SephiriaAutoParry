using System;
using System.IO;
using System.Reflection;
class CallbackTests
{
    static string Game;
    static int Main(string[] args)
    {
        Game = args[0];
        string dist = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new [] { Path.Combine(Game, @"Sephiria_Data\Managed"), Path.Combine(Game, @"BepInEx\core"), dist }) {
                string path = Path.Combine(dir,name);
                if(File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        var game = Assembly.LoadFrom(Path.Combine(Game, @"Sephiria_Data\Managed\Assembly-CSharp.dll"));
        var plugin = Assembly.LoadFrom(Path.Combine(dist, "SephiriaAutoParry.dll"));
        var classify = plugin.GetType("SephiriaAutoParry.AttackCallback").GetMethod("IsAttack",
            BindingFlags.NonPublic | BindingFlags.Static, null, new [] {typeof(MethodInfo)}, null);
        Check(game,classify,"FireTrap","FireBulletAnimation",true);
        Check(game,classify,"FireTrap","EndAttackAnimation",false);
        Check(game,classify,"Unit_KnightDemon","FireAni",true);
        Check(game,classify,"Unit_KnightDemonAnimation","FireAni",true);
        Check(game,classify,"Unit_KnightDemonAnimation","EndAtkAni",false);
        Check(game,classify,"Unit_Askard","CreateImpactAttack",true);
        Check(game,classify,"Unit_BabaMerchant","Stamp",true);
        Check(game,classify,"Unit_Bat","CreateAttack",true);
        Check(game,classify,"Unit_CapybaraFanatic","FireAnimation",true);
        Check(game,classify,"Unit_CatAssassin","BeginFireAnimation",true);
        Check(game,classify,"Unit_BirdDemon","RapidAttackCoroutine",true);
        Check(game,classify,"Unit_DemonBook","SmashCoroutine",true);
        var harmony = Assembly.LoadFrom(Path.Combine(Game, @"BepInEx\core\0Harmony.dll"));
        var reader = harmony.GetType("HarmonyLib.PatchProcessor").GetMethod("GetOriginalInstructions",
            new [] { typeof(MethodBase), typeof(System.Reflection.Emit.ILGenerator) });
        var prefix = plugin.GetType("SephiriaAutoParry.IncomingAttackInput").GetMethod("Prefix", BindingFlags.NonPublic|BindingFlags.Static);
        var calls = new System.Collections.Generic.List<string>();
        foreach (var instruction in (System.Collections.IEnumerable)reader.Invoke(null,new object[]{prefix,null})) {
            var operand = instruction.GetType().GetField("operand").GetValue(instruction) as MethodBase;
            if(operand != null) calls.Add(operand.Name);
        }
        if(calls.IndexOf("Defend") < 0 || calls.IndexOf("CancelIfNotStarted") <= calls.IndexOf("Defend"))
            throw new Exception("Impact prefix must attempt input then discard unstarted input.");
        if(plugin.GetType("SephiriaAutoParry.IncomingAttackInput").GetMethod("Postfix", BindingFlags.NonPublic|BindingFlags.Static) != null)
            throw new Exception("Automatic input must not execute in a damage postfix.");
        Console.WriteLine("Pre-damage input/cancellation order checks passed.");
        var defend = plugin.GetType("SephiriaAutoParry.Plugin").GetMethod("Defend", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var instruction in (System.Collections.IEnumerable)reader.Invoke(null, new object[] { defend, null })) {
            var operand = instruction.GetType().GetField("operand").GetValue(instruction) as MethodBase;
            if (operand != null && (operand.Name == "set_NetworkisBladeSheathed" || operand.Name == "set_NetworksheathStateEnabled"))
                throw new Exception("Auto defense must not synthesize katana sheath state.");
        }
        Console.WriteLine("Katana defense does not overwrite native sheath state.");
        foreach (string className in new [] { "Plugin", "ActionTicket" })
        foreach (var method in plugin.GetType("SephiriaAutoParry." + className).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
            foreach (var instruction in (System.Collections.IEnumerable)reader.Invoke(null, new object[] { method, null })) {
                var operand = instruction.GetType().GetField("operand").GetValue(instruction) as MethodBase;
                if (operand != null && ((method.Name != "TryForceCancel" && (operand.Name == "CancelAction" || operand.Name == "CancelCurrentAction" || operand.Name == "StopDash")) ||
                    (operand.DeclaringType.FullName == "UnityEngine.Animator" && operand.Name == "Update")))
                    throw new Exception("Automatic input must not force action/animation changes: " + operand.Name);
            }
        }
        Console.WriteLine("No forced animator evaluation; action cancellation is isolated to the opt-in helper.");
        Console.WriteLine("All attack callback classification checks passed.");
        return 0;
    }
    static void Check(Assembly game, MethodInfo classify, string type, string method, bool expected)
    {
        var target = game.GetType(type).GetMethod(method,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
        bool actual = (bool)classify.Invoke(null,new object[]{target});
        Console.WriteLine(type+"."+method+": "+actual);
        if(actual!=expected) throw new Exception("Unexpected classification: "+type+"."+method);
    }
}


