using System;
using System.Collections.Generic;

namespace DesktopPet
{
    /// <summary>
    /// Registry of pet TYPES loaded "alongside" the active/default pet, so several different pets can be
    /// on screen at once. Each entry owns a validated (Xml, Animations) pair that every on-screen pet of
    /// that type shares; a reference count tracks how many pets use it, and the pair is disposed only
    /// when the last pet of that type closes. FormPet treats its Animations/Xml as borrowed refs and
    /// never disposes them, so ownership lives here.
    ///
    /// The active/default type is NOT held here — StartUp owns it directly (its xml/animations fields)
    /// and pins its lifetime — so only extra types are reference-counted. All access is on the UI thread
    /// (spawns from the tick/tray, decrements from FormClosed), so no locking is needed.
    /// </summary>
    internal sealed class PetTypeRegistry
    {
        internal sealed class Entry
        {
            public string Id;
            public Xml Xml;
            public Animations Animations;
            public int RefCount;
        }

        private readonly Dictionary<string, Entry> _byId =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        internal bool TryGet(string id, out Entry entry)
        {
            return _byId.TryGetValue(id ?? "", out entry);
        }

        /// <summary>Cache a freshly staged type. Reference count starts at 0 until a pet is spawned.</summary>
        internal Entry Add(string id, Xml xml, Animations animations)
        {
            var entry = new Entry { Id = id ?? "", Xml = xml, Animations = animations, RefCount = 0 };
            _byId[entry.Id] = entry;
            return entry;
        }

        internal void Increment(Entry entry)
        {
            if (entry != null) entry.RefCount++;
        }

        /// <summary>Release one pet's use of a type; dispose the shared pair when the last pet closes.</summary>
        internal void Decrement(Entry entry)
        {
            if (entry == null) return;
            entry.RefCount--;
            if (entry.RefCount <= 0)
                DisposeEntry(entry);
        }

        /// <summary>Drop a type that was staged but never spawned (e.g. the spawn slot was full).</summary>
        internal void DropIfUnused(Entry entry)
        {
            if (entry != null && entry.RefCount <= 0)
                DisposeEntry(entry);
        }

        internal IEnumerable<Entry> Entries { get { return _byId.Values; } }

        internal void DisposeAll()
        {
            var entries = new List<Entry>(_byId.Values);
            _byId.Clear();
            foreach (Entry entry in entries)
                DisposePair(entry);
        }

        private void DisposeEntry(Entry entry)
        {
            _byId.Remove(entry.Id);
            DisposePair(entry);
        }

        private static void DisposePair(Entry entry)
        {
            // Xml.Dispose/Animations.Dispose are idempotent, so a double-dispose is harmless.
            if (entry.Animations != null) { entry.Animations.Dispose(); entry.Animations = null; }
            if (entry.Xml != null) { entry.Xml.Dispose(); entry.Xml = null; }
        }
    }
}
