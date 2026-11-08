using System;

namespace ET.Client
{
    /// <summary>Local application service. ET owns its lifetime; views only issue commands.</summary>
    public sealed class PingDouSession
    {
        public PingDouPattern[] Catalog { get; }
        public PingDouSaveStore Store { get; }
        public PingDouRound Round { get; private set; }
        public bool Dirty { get; private set; }

        public event Action Changed;
        public event Action<PingDouPlacement> Placed;
        public PingDouSession(PingDouPattern[] catalog, PingDouSaveStore store)
        {
            Catalog = catalog;
            Store = store;
            Open(Array.FindIndex(catalog, p => p.id == store.Profile.currentId), false);
        }

        public void Open(int index, bool restart)
        {
            if (Round != null)
                Flush();
            var pattern = Catalog[Math.Max(0, Math.Min(Catalog.Length - 1, index))];
            Round = restart ? new PingDouRound(pattern) : Store.Open(pattern);
            if (Round.Phase == PingDouPhase.Complete)
                Store.Collect(Round);
            Dirty = true;
            Changed?.Invoke();
        }

        public void Commit(int index)
        {
            // The presenter reserves the color when the bead starts its drop.
            if (!Round.TryPlace(index, Round.Key(index), out var placement))
                return;
            Dirty = true;
            Placed?.Invoke(placement);
            Changed?.Invoke();
        }

        public void BeginIroning()
        {
            if (Round.BeginIroning())
                Notify();
        }

        public void Heat(int index, float amount)
        {
            if (Round.ApplyHeat(index, amount))
                Notify();
        }

        public void FinishAssisted()
        {
            if (Round.FinishIroningAssisted())
                Notify();
        }

        public void Reveal()
        {
            if (Round.Reveal())
            {
                Store.Collect(Round);
                Notify();
                Flush();
            }
        }

        public void Notify()
        {
            Dirty = true;
            Changed?.Invoke();
        }

        public void Flush()
        {
            if (Round != null && Dirty)
            {
                Store.Save(Round);
                Dirty = false;
            }
        }

        public int CurrentIndex => Array.FindIndex(Catalog, p => p.id == Round.Pattern.id);

        public int NextIndex
        {
            get
            {
                for (int offset = 1; offset < Catalog.Length; ++offset)
                {
                    int index = (CurrentIndex + offset) % Catalog.Length;
                    if (Catalog[index].theme == Round.Pattern.theme && !Store.Profile.records.Exists(r => r.id == Catalog[index].id))
                        return index;
                }

                return (CurrentIndex + 1) % Catalog.Length;
            }
        }

        public void Next() => Open(NextIndex, true);
    }
}
