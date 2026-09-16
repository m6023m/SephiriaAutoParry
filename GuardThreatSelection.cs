using System;

namespace SephiriaAutoParry
{
    // Kept independent of Unity so ordering and direction math can be tested directly.
    internal sealed class GuardThreatSelection
    {
        internal bool HasValue;
        internal float Deadline, X, Y;
        internal string Threat;
        internal void Clear() { HasValue = false; }
        internal bool Offer(float deadline, float x, float y, string threat)
        {
            float length = (float)Math.Sqrt(x * x + y * y);
            if (Single.IsNaN(deadline) || Single.IsInfinity(deadline) ||
                Single.IsNaN(length) || Single.IsInfinity(length) || length < 0.0001f ||
                (HasValue && deadline >= Deadline)) return false;
            HasValue = true;
            Deadline = deadline; X = x / length; Y = y / length; Threat = threat;
            return true;
        }
    }
}
