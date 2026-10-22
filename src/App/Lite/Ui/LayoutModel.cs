// MwbLite: the PC layout being edited in Settings: MWB's 4 slots (row-major), one row or a 2×2 grid. Pure logic,
// shared by the drag-and-drop view (LayoutEditorView) and the tests.
using System;
using System.Linq;

namespace MouseWithoutBorders.Lite.Ui
{
    internal sealed class LayoutModel
    {
        private readonly string[] slots = Enumerable.Repeat(string.Empty, LiteConfig.MaxMachines).ToArray();
        private bool oneRow = true;
        private int selected;

        /// <summary>Raised when a PC moves, is added, renamed or removed (not for one row / two rows).</summary>
        internal event EventHandler LayoutChanged;

        /// <summary>Raised on any change the view must redraw (including selection and one row / two rows).</summary>
        internal event EventHandler Redraw;

        /// <summary>This computer's name, highlighted in the layout.</summary>
        internal string ThisPc { get; set; } = string.Empty;

        internal bool OneRow
        {
            get => oneRow;
            set
            {
                oneRow = value;
                Redraw?.Invoke(this, EventArgs.Empty);
            }
        }

        internal int Columns => oneRow ? LiteConfig.MaxMachines : 2;

        internal int Rows => slots.Length / Columns;

        internal int Selected
        {
            get => selected;
            set
            {
                selected = Math.Clamp(value, 0, slots.Length - 1);
                Redraw?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>The 4 slots in MWB order (row-major), "" for empty.</summary>
        internal string[] Slots
        {
            get => (string[])slots.Clone();
            set
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    slots[i] = value != null && i < value.Length ? LiteConfig.NormalizeName(value[i]) : string.Empty;
                }

                Redraw?.Invoke(this, EventArgs.Empty);
            }
        }

        internal string this[int index] => slots[index];

        internal bool IsThisPc(int index) => slots[index].Length > 0 && slots[index].Equals(ThisPc, StringComparison.OrdinalIgnoreCase);

        /// <summary>Moves the PC in <paramref name="from"/> to <paramref name="to"/>, swapping with what is there.</summary>
        internal bool MoveTile(int from, int to)
        {
            if (from == to || from < 0 || to < 0 || from >= slots.Length || to >= slots.Length || slots[from].Length == 0)
            {
                return false;
            }

            (slots[from], slots[to]) = (slots[to], slots[from]);
            selected = to;
            Changed();
            return true;
        }

        /// <summary>Puts a name in the first empty slot. Returns false if it is already there or all slots are used.</summary>
        internal bool AddName(string name)
        {
            name = LiteConfig.NormalizeName(name);
            if (name.Length == 0 || slots.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            int free = Array.FindIndex(slots, s => s.Length == 0);
            if (free < 0)
            {
                return false;
            }

            slots[free] = name;
            selected = free;
            Changed();
            return true;
        }

        /// <summary>Renames (or fills) a slot; "" empties it.</summary>
        internal void SetName(int index, string name)
        {
            name = LiteConfig.NormalizeName(name);
            if (index >= 0 && index < slots.Length && !string.Equals(name, slots[index], StringComparison.Ordinal))
            {
                slots[index] = name;
                Changed();
            }
        }

        internal void RemoveAt(int index)
        {
            if (index >= 0 && index < slots.Length && slots[index].Length > 0)
            {
                slots[index] = string.Empty;
                Changed();
            }
        }

        /// <summary>Slot index in direction (dx, dy) from index, or -1 at the edge of the grid.</summary>
        internal int Neighbour(int index, int dx, int dy)
        {
            int col = (index % Columns) + dx;
            int row = (index / Columns) + dy;
            return col < 0 || col >= Columns || row < 0 || row >= Rows ? -1 : (row * Columns) + col;
        }

        private void Changed()
        {
            Redraw?.Invoke(this, EventArgs.Empty);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
