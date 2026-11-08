using System;
using System.Collections.Generic;

namespace ET.Client
{
    public enum PingDouPhase
    {
        Placing,
        ReadyToIron,
        Ironing,
        ReadyToReveal,
        Complete
    }

    [Serializable]
    public sealed class PingDouColor
    {
        public string key;
        public string name;
        public string hex;
    }

    [Serializable]
    public sealed class PingDouPattern
    {
        public string id;
        public string name;
        public string theme;
        public int size;
        public PingDouColor[] colors;
        public string[] rows;
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(id) || size < 2 || size > 64 || rows == null || rows.Length != size || colors == null || colors.Length == 0)
                throw new ArgumentException("Invalid PingDou pattern header.");
            var keys = new HashSet<char>();
            foreach (var color in colors)
                if (color == null || color.key == null || color.key.Length != 1 || color.key[0] == '.' || !keys.Add(color.key[0]))
                    throw new ArgumentException("Duplicate or invalid palette key.");
            int count = 0;
            foreach (string row in rows)
            {
                if (row == null || row.Length != size)
                    throw new ArgumentException("Every row must use the fixed grid size.");
                foreach (char key in row)
                {
                    if (key == '.')
                        continue;
                    if (!keys.Contains(key))
                        throw new ArgumentException("Unknown palette key.");
                    ++count;
                }
            }

            if (count == 0)
                throw new ArgumentException("Empty PingDou pattern.");
        }
    }

    [Serializable]
    public sealed class PingDouSnapshot
    {
        public int version = 1;
        public string patternId;
        public string signature;
        public bool[] placed;
        public float[] heat;
        public PingDouPhase phase;
        public float effectiveHeat;
        public float totalHeat;
        public bool assisted;
    }

    public readonly struct PingDouPlacement
    {
        public readonly int Index, Points, Combo;
        public readonly bool ColorCompleted;
        public PingDouPlacement(int index, int points, int combo, bool colorCompleted)
        {
            Index = index;
            Points = points;
            Combo = combo;
            ColorCompleted = colorCompleted;
        }
    }

    /// <summary>
    /// Authoritative local round for the demo. No Unity dependency, clocks, input or presentation.
    /// Reserve a cell in the presenter; commit only when its drop animation reaches the board.
    /// Scores are derived from accepted cells, including when restoring an untrusted local save.
    /// </summary>
    public sealed class PingDouRound
    {
        private readonly bool[] placed;
        private readonly float[] heat;
        private readonly Dictionary<char, int> remaining = new Dictionary<char, int>();
        private int heated;
        private float effectiveHeat, totalHeat;
        public PingDouPattern Pattern { get; }
        public PingDouPhase Phase { get; private set; }
        public int Total { get; private set; }
        public int Placed { get; private set; }
        public int BaseScore => Placed * 10;
        public int ComboScore { get; private set; }
        public int ColorScore { get; private set; }
        public int Score => BaseScore + ComboScore + ColorScore;
        public bool Assisted { get; private set; }
        public int ComboTarget => Math.Min(20, Total);
        public float HeatEfficiency => totalHeat <= 0 ? 0 : effectiveHeat / totalHeat;
        public int HeatPercent => heated == Total ? 100 : Math.Min(99, (int)(HeatSum() / Total * 100));
        public int Stars => Phase != PingDouPhase.Complete ? 0 : 1 + (Placed >= ComboTarget ? 1 : 0) + (!Assisted && HeatEfficiency >= .55f ? 1 : 0);

        public PingDouRound(PingDouPattern pattern)
        {
            pattern.Validate();
            Pattern = pattern;
            placed = new bool[pattern.size * pattern.size];
            heat = new float[placed.Length];
            foreach (var color in pattern.colors)
                remaining[color.key[0]] = 0;
            foreach (string row in pattern.rows)
                foreach (char key in row)
                    if (key != '.')
                    {
                        ++remaining[key];
                        ++Total;
                    }
        }

        public char Key(int index) => index < 0 || index >= placed.Length ? '.' : Pattern.rows[index / Pattern.size][index % Pattern.size];
        public bool IsPlaced(int index) => index >= 0 && index < placed.Length && placed[index];
        public float Heat(int index) => index >= 0 && index < heat.Length ? heat[index] : 0;
        public int Remaining(char key) => remaining.TryGetValue(key, out int count) ? count : 0;
        public bool CanPlace(int index, char color) => Phase == PingDouPhase.Placing && Key(index) != '.' && Key(index) == color && !IsPlaced(index);
        public bool TryPlace(int index, char color, out PingDouPlacement result)
        {
            result = default;
            if (!CanPlace(index, color))
                return false;
            placed[index] = true;
            ++Placed;
            int bonus = Placed >= 20 ? 10 : Placed >= 10 ? 5 : Placed >= 5 ? 2 : 0;
            ComboScore += bonus;
            bool complete = --remaining[color] == 0;
            if (complete)
                ColorScore += 50;
            if (Placed == Total)
                Phase = PingDouPhase.ReadyToIron;
            result = new PingDouPlacement(index, 10 + bonus, Placed, complete);
            return true;
        }

        public bool BeginIroning()
        {
            if (Phase != PingDouPhase.ReadyToIron)
                return false;
            Phase = PingDouPhase.Ironing;
            return true;
        }

        public bool ApplyHeat(int index, float amount)
        {
            if (Phase != PingDouPhase.Ironing || !IsPlaced(index) || amount <= 0 || float.IsNaN(amount) || float.IsInfinity(amount))
                return false;
            totalHeat += amount;
            float before = heat[index];
            heat[index] = Math.Min(1, before + amount);
            effectiveHeat += heat[index] - before;
            if (before < 1 && heat[index] >= 1)
                ++heated;
            if (heated == Total)
                Phase = PingDouPhase.ReadyToReveal;
            return heat[index] > before;
        }

        public bool FinishIroningAssisted()
        {
            if (Phase != PingDouPhase.Ironing)
                return false;
            Assisted = true;
            for (int i = 0; i < heat.Length; ++i)
                if (IsPlaced(i) && heat[i] < 1)
                    ApplyHeat(i, 1 - heat[i]);
            return true;
        }

        public bool Reveal()
        {
            if (Phase != PingDouPhase.ReadyToReveal)
                return false;
            Phase = PingDouPhase.Complete;
            return true;
        }

        private float HeatSum()
        {
            float sum = 0;
            foreach (float value in heat)
                sum += value;
            return sum;
        }

        private string Signature() => Pattern.id + ":" + string.Join("/", Pattern.rows);
        public PingDouSnapshot Capture() => new PingDouSnapshot
        {
            patternId = Pattern.id,
            signature = Signature(),
            placed = (bool[])placed.Clone(),
            heat = (float[])heat.Clone(),
            phase = Phase,
            effectiveHeat = effectiveHeat,
            totalHeat = totalHeat,
            assisted = Assisted
        };
        public static PingDouRound Restore(PingDouPattern pattern, PingDouSnapshot save)
        {
            var round = new PingDouRound(pattern);
            if (save == null || save.version != 1 || save.patternId != pattern.id || save.signature != round.Signature() || save.placed == null || save.heat == null || save.placed.Length != round.placed.Length || save.heat.Length != round.heat.Length)
                return round;
            for (int i = 0; i < save.placed.Length; ++i)
                if (save.placed[i])
                    round.TryPlace(i, round.Key(i), out _);
            if (round.Phase != PingDouPhase.ReadyToIron || save.phase < PingDouPhase.Ironing || save.phase > PingDouPhase.Complete)
                return round;
            round.BeginIroning();
            for (int i = 0; i < save.heat.Length; ++i)
                if (!float.IsNaN(save.heat[i]) && !float.IsInfinity(save.heat[i]))
                    round.ApplyHeat(i, Math.Max(0, Math.Min(1, save.heat[i])));
            round.Assisted = save.assisted;
            if (!float.IsNaN(save.totalHeat) && !float.IsInfinity(save.totalHeat))
                round.totalHeat = Math.Max(round.effectiveHeat, save.totalHeat);
            if (save.phase == PingDouPhase.Complete)
                round.Reveal();
            return round;
        }
    }
}
