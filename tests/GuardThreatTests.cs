using System;
using SephiriaAutoParry;
class GuardThreatTests
{
    static int checks;
    static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS " + label); checks++;
    }
    static int Main()
    {
        var choice = new GuardThreatSelection();
        Check(choice.Offer(10.15f, -1, 0, "left later"), "first candidate accepted");
        Check(choice.Offer(10.04f, 1, 0, "right sooner"), "earlier opposite attack replaces later attack");
        Check(choice.X == 1 && choice.Y == 0 && choice.Threat == "right sooner", "face right for earliest threat");
        Check(!choice.Offer(10.14f, -1, 0, "left later again"), "later callback cannot turn guard away");
        Check(!choice.Offer(10.04f, -1, 0, "simultaneous opposite"), "exact tie holds one direction without flicker");
        choice.Clear();
        Check(choice.Offer(10.04f, 1, 0, "right sooner") && !choice.Offer(10.15f, -1, 0, "left later"), "reverse callback order produces same winner");
        choice.Clear();
        Check(!choice.Offer(1, 0, 0, "unknown direction") && !choice.HasValue, "unknown direction does not invent a facing");
        Check(!choice.Offer(Single.NaN, 1, 0, "invalid time"), "NaN deadline rejected");
        Check(!choice.Offer(1, Single.PositiveInfinity, 0, "invalid motion"), "infinite direction rejected");
        Check(choice.Offer(1, 3, 4, "diagonal") && Math.Abs(choice.X - .6f) < .0001f && Math.Abs(choice.Y - .8f) < .0001f, "diagonal normalized without changing angle");
        choice.Clear();
        Check(!choice.HasValue && choice.Offer(20, 0, -1, "next frame"), "frame reset discards previous threat");
        Console.WriteLine(checks + " guard selection checks passed.");
        return 0;
    }
}
