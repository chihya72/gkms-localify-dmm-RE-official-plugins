using System.Numerics;

namespace Gkms.Plugins.Camera;

// Direct formulas from camera.cpp and Hook.cpp TryLookRotationQuat.
public static class CameraMath
{
    private const float Rad = MathF.PI / 180;
    public static Vector3 Orbit(Vector3 target, Vector3 offset) => target + new Vector3(offset.Z * MathF.Sin(offset.X * Rad), offset.Y, offset.Z * MathF.Cos(offset.X * Rad));
    public static Vector3 FirstPerson(Vector3 position, Vector3 forward, Vector3 offset)
    {
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-8f) return position + new Vector3(0, offset.Y, 0);
        return position - forward * offset.Z - Vector3.Normalize(right) * offset.X + new Vector3(0, offset.Y, 0);
    }
    public static Vector3 FollowLook(Vector3 target, Vector3 offset, Vector2 look, float y)
    {
        var angle = (offset.X + (offset.Z >= 0 ? 90 : -90)) * Rad;
        return new(target.X + look.X * MathF.Sin(angle), y + look.Y, target.Z + look.X * MathF.Cos(angle));
    }
    public static bool Look(Vector3 forward, out Quaternion value)
    {
        value = Quaternion.Identity;
        if (!Finite(forward) || forward.LengthSquared() < 1e-8f) return false;
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(Vector3.UnitY, forward);
        if (right.LengthSquared() < 1e-8f) return false;
        right = Vector3.Normalize(right);
        var up = Vector3.Cross(forward, right);
        // System.Numerics uses row vectors; the same axes are the columns in Unity.
        value = Quaternion.CreateFromRotationMatrix(new(right.X,right.Y,right.Z,0, up.X,up.Y,up.Z,0, forward.X,forward.Y,forward.Z,0, 0,0,0,1));
        return true;
    }
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public sealed class HeightState { internal float? Last; }
    public sealed class Height
    {
        private readonly Queue<float> values = new();
        private readonly HeightState state;
        public Height(HeightState? state = null) => this.state = state ?? new();
        private float sum;
        public float Apply(float y, bool record, bool smooth)
        {
            ref var last = ref state.Last;
            last ??= y;
            if (!float.IsFinite(y)) return float.IsFinite(last.Value) ? last.Value : 0;
            if (!float.IsFinite(last.Value)) last = y;
            if (!smooth) return (last = y).Value;
            var average = values.Count == 0 ? 0 : sum / values.Count;
            if (record) { if (values.Count == 60) sum -= values.Dequeue(); values.Enqueue(y); sum += y; }
            if (!float.IsFinite(average) || Math.Abs(y - average) < .02f) return last.Value;
            last += ((values.Count == 0 ? 0 : sum / values.Count) - last.Value) / 8;
            return last.Value;
        }
        public void Clear() { values.Clear(); sum = 0; state.Last = null; }
    }
}
