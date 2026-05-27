// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps.GridHighLight;
using ClassicUO.Network;
using ClassicUO.Network.PacketHandlers.Helpers;
using ClassicUO.Utility;

namespace ClassicUO.Game.Managers
{
    public sealed class ObjectPropertiesListManager
    {
        private readonly Dictionary<uint, ItemProperty> _itemsProperties = new Dictionary<uint, ItemProperty>();
        private World _world;

        public ObjectPropertiesListManager(World world)
        {
            _world = world;
        }

        public void Add(uint serial, uint revision, string name, string data, int namecliloc)
        {
            // Preserve any LabelData that ForcedTooltipManager has accumulated for this item
            // so server-driven OPL refreshes (which can arrive unsolicited and overwrite Data)
            // don't wipe out our single-click-derived property text.
            string preservedLabel = null;
            if (_itemsProperties.TryGetValue(serial, out ItemProperty existing))
            {
                preservedLabel = existing.LabelData;
            }

            if (!_itemsProperties.TryGetValue(serial, out ItemProperty prop))
            {
                prop = new ItemProperty();
                _itemsProperties[serial] = prop;
            }

            prop.Serial = serial;
            prop.Revision = revision;
            prop.Name = name;
            prop.Data = data;
            prop.LabelData = preservedLabel;
            prop.NameCliloc = namecliloc;

            EventSink.InvokeOPLOnReceive(null, new OPLEventArgs(serial, name, data));

            Item item = _world.Items.Get(serial);
            if(item != null)
                ItemDatabaseManager.Instance.AddOrUpdateItem(item, _world);
        }

        public bool Contains(uint serial)
        {
            if (ProfileManager.CurrentProfile != null && (ProfileManager.CurrentProfile.ForceTooltipsOnOldClients || ProfileManager.CurrentProfile.MergeSingleClickIntoTooltip))
                ForcedTooltipManager.RequestName(_world, serial);

            if (_itemsProperties.TryGetValue(serial, out ItemProperty p))
                return true; //p.Revision != 0;  <-- revision == 0 can contain the name.

            // if we don't have the OPL of this item, let's request it to the server.
            // Original client seems asking for OPL when character is not running.
            // We'll ask OPL when mouse is over an object.
            SharedStore.AddMegaCliLocRequest(serial);

            return false;
        }

        public bool IsRevisionEquals(uint serial, uint revision)
        {
            if (_itemsProperties.TryGetValue(serial, out ItemProperty prop))
            {
                return (revision & ~0x40000000) == prop.Revision || // remove the mask
                       revision == prop.Revision;                   // if mask removing didn't work, try a simple compare.
            }

            return false;
        }

        public bool TryGetRevision(uint serial, out uint revision)
        {
            if (_itemsProperties.TryGetValue(serial, out ItemProperty p))
            {
                revision = p.Revision;

                return true;
            }

            revision = 0;

            return false;
        }

        public bool TryGetNameAndData(uint serial, out string name, out string data)
        {
            if (_itemsProperties.TryGetValue(serial, out ItemProperty p))
            {
                name = p.Name;
                string label = FilterRedundantLabelLines(p.LabelData, p.Data, p.Name);
                if (string.IsNullOrEmpty(label))
                    data = p.Data;
                else if (string.IsNullOrEmpty(p.Data))
                    data = label;
                else
                    data = p.Data + "\n" + label;

                return true;
            }

            name = data = null;

            return false;
        }

        // Filter LabelData lines that have become redundant against the current Name/Data.
        // Run at read time (not write time) so we catch the case where a label was stored
        // before the server's OPL Data fully populated — once Data fills in, the filter
        // suppresses any line that's now duplicated.
        private static string FilterRedundantLabelLines(string labelData, string data, string name)
        {
            if (string.IsNullOrEmpty(labelData)) return labelData;

            bool chargeable = (data?.IndexOf("Charges", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                              || (name?.IndexOf("Charges", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

            var keep = new System.Text.StringBuilder();
            foreach (string raw in labelData.Split('\n'))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Charge-summary lines like "[Item Identification : 124]" duplicate the
                // "Charges:" line already in Data on chargeable items.
                if (chargeable && line.Length >= 4 && line[0] == '[' && line[line.Length - 1] == ']' && line.Contains(':'))
                    continue;

                // Skill-fail messages like "This lock cannot be picked by normal means"
                // sometimes ended up in LabelData on older builds. Strip them at read
                // time so previously-polluted chests stop showing the line in their
                // tooltip after the user upgrades. (New writes are filtered at AddLabel
                // by the ForcedTooltipManager puzzle-detect path.)
                if (PickedChestRegistry.IsPuzzleLockFailMessage(line))
                    continue;

                // The literal label or its bracket-stripped form is already in Name/Data.
                if (NormalizeContains(name, line) || NormalizeContains(data, line))
                    continue;

                if (keep.Length > 0) keep.Append('\n');
                keep.Append(line);
            }
            return keep.Length == 0 ? null : keep.ToString();
        }

        // Accumulates single-click label text into a side-cache that survives server-driven Add()
        // calls. Used by ForcedTooltipManager when MergeSingleClickIntoTooltip is on.
        //
        // A single user single-click triggers a *burst* of label packets from the server (one
        // per property line — e.g. "Magic Wand", "[Unidentified]", "Charges: 126"). We accumulate
        // packets that arrive within LABEL_BURST_WINDOW ms of the last one as a single session.
        // The first label of a new burst (gap > window) REPLACES the prior LabelData, so stale
        // state (e.g. "[Unidentified]" after the item is identified, or "Charges: 126" after a
        // charge is consumed) gets cleared on the next click instead of accumulating forever.
        private const uint LABEL_BURST_WINDOW = 250;

        public void AddLabel(uint serial, string labelText)
        {
            if (string.IsNullOrEmpty(labelText))
                return;
            labelText = labelText.Trim();
            if (string.IsNullOrEmpty(labelText))
                return;
            if (!_itemsProperties.TryGetValue(serial, out ItemProperty prop))
                return;

            // Don't duplicate text the server's OPL already provides. The shard may pack
            // rich info into either Name or Data, so check both. Normalize whitespace (the
            // server can split logical lines across cliloc segments with "\n" while the
            // single-click label sends them as one string) and ignore case.
            string normalizedLabel = labelText.Replace('\n', ' ');
            if (NormalizeContains(prop.Name, normalizedLabel) || NormalizeContains(prop.Data, normalizedLabel))
            {
                // Skipped labels intentionally do NOT advance LastLabelTick — otherwise the
                // next legitimate label in the same server burst would compute a tiny gap and
                // append onto stale LabelData instead of replacing it.
                return;
            }

            // Bracketed charge-summary labels like "[Item Identification : 124]" reuse the
            // spell name + count, but Data has the same count under a different phrasing
            // ("Identification Charges: 124"). When the item is chargeable (Data/Name mentions
            // "Charges"), suppress the bracketed summary — it's redundant. Quality brackets
            // like "[Exceptional/Massive]" don't contain a colon, so this rule leaves them alone.
            if (labelText.Length >= 4 && labelText[0] == '[' && labelText[labelText.Length - 1] == ']' && labelText.Contains(':'))
            {
                bool chargeable = (prop.Data?.IndexOf("Charges", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                                  || (prop.Name?.IndexOf("Charges", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
                if (chargeable) return;
            }

            bool newBurst = string.IsNullOrEmpty(prop.LabelData)
                            || (Time.Ticks - prop.LastLabelTick) > LABEL_BURST_WINDOW;

            if (newBurst)
            {
                prop.LabelData = labelText;
            }
            else if (prop.LabelData.IndexOf(labelText) < 0)
            {
                prop.LabelData = prop.LabelData + "\n" + labelText;
            }
            else
            {
                return;
            }

            prop.LastLabelTick = Time.Ticks;

            // Bump revision into the near future so Tooltip.Draw re-reads. The next genuine
            // server-driven Add() will still take precedence by overwriting Revision.
            prop.Revision = Time.Ticks + 1500;

            EventSink.InvokeOPLOnReceive(null, new OPLEventArgs(serial, prop.Name, string.IsNullOrEmpty(prop.Data) ? prop.LabelData : prop.Data + "\n" + prop.LabelData));

            Item item = _world.Items.Get(serial);
            if (item != null)
                ItemDatabaseManager.Instance.AddOrUpdateItem(item, _world);
        }

        private static bool NormalizeContains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
                return false;
            string h = haystack.Replace('\n', ' ').Replace("<br>", " ");
            if (h.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // The shard often sends bracketed summary labels like "[Lizardman Slaughter]" or
            // "[Item Identification : 124]" where the unbracketed property already appears in
            // Data ("Lizardman Slaughter") or Name. Try matching the bracket-stripped form too.
            string trimmedNeedle = needle.Trim();
            if (trimmedNeedle.Length >= 2 && trimmedNeedle[0] == '[' && trimmedNeedle[trimmedNeedle.Length - 1] == ']')
            {
                string inner = trimmedNeedle.Substring(1, trimmedNeedle.Length - 2).Trim();
                if (inner.Length > 0 && h.IndexOf(inner, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public int GetNameCliloc(uint serial)
        {
            if (_itemsProperties.TryGetValue(serial, out ItemProperty p))
            {
                return p.NameCliloc;
            }

            return 0;
        }

        public ItemPropertiesData TryGetItemPropertiesData(World world, uint serial)
        {
            if (Contains(serial))
                if (world.Items.TryGetValue(serial, out Item item))
                    return new ItemPropertiesData(world, item);
            return null;
        }

        public void Remove(uint serial) => _itemsProperties.Remove(serial);

        public void Clear() => _itemsProperties.Clear();
    }

    public class ItemProperty
    {
        public bool IsEmpty => string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(Data) && string.IsNullOrEmpty(LabelData);
        public string Data;
        public string Name;
        // LabelData holds single-click-derived property text that gets merged into the tooltip
        // at read time. Stored separately from Data so server-driven Add() calls don't wipe it.
        public string LabelData;
        // Time.Ticks of the last AddLabel call; used to group same-burst response packets.
        public uint LastLabelTick;
        public uint Revision;
        public uint Serial;
        public int NameCliloc;

        public string CreateData(bool extended) => string.Empty;
    }

    public class ItemPropertiesData
    {
        public readonly bool HasData = false;
        public string Name = "";
        public readonly string RawData = "";
        public readonly uint serial;
        public string[] RawLines;
        public readonly Item item, itemComparedTo;
        public List<SinglePropertyData> singlePropertyData = new List<SinglePropertyData>();

        private World world;

        public ItemPropertiesData(World world, Item item, Item compareTo = null)
        {
            if (item == null)
                return;
            this.world = world;
            this.item = item;
            itemComparedTo = compareTo;

            serial = item.Serial;
            if (world.OPL.TryGetNameAndData(item.Serial, out Name, out RawData))
            {
                Name = Name.Trim();
                HasData = true;
                processData();
            }
        }

        public ItemPropertiesData(string tooltip)
        {
            if (string.IsNullOrEmpty(tooltip))
                return;
            if (tooltip.Contains("\n"))
            {
                Name = tooltip.Substring(0, tooltip.IndexOf("\n"));
                RawData = tooltip.Substring(tooltip.IndexOf("\n") + 1);
            }
            else
            {
                Name = tooltip;
            }
            HasData = true;
            processData();
        }

        private void processData()
        {
            string formattedData = TextBox.ConvertHtmlToFontStashSharpCommand(RawData);

            RawLines = formattedData.Split(new string[] { "\n", "<br>" }, StringSplitOptions.None);

            foreach (string line in RawLines)
            {
                singlePropertyData.Add(new SinglePropertyData(line));
            }

            if (itemComparedTo != null)
            {
                GenComparisonData();
            }
        }

        private void GenComparisonData()
        {
            if (itemComparedTo == null) return;

            var itemPropertiesData = new ItemPropertiesData(world, itemComparedTo);
            if (itemPropertiesData.HasData)
            {
                foreach (SinglePropertyData thisItem in singlePropertyData)
                {
                    foreach (SinglePropertyData secondItem in itemPropertiesData.singlePropertyData)
                    {
                        if (String.Equals(thisItem.Name, secondItem.Name, StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (thisItem.FirstValue != double.MinValue && secondItem.FirstValue != double.MinValue)
                            {
                                thisItem.FirstDiff = thisItem.FirstValue - secondItem.FirstValue;
                            }

                            if (thisItem.SecondValue > double.MinValue && secondItem.SecondValue > double.MinValue)
                            {
                                thisItem.SecondDiff = thisItem.SecondValue - secondItem.SecondValue;
                            }
                            break;
                        }
                    }
                }
            }
        }

        public bool GenerateComparisonTooltip(ItemPropertiesData comparedTo, out string compiledToolTip)
        {
            if (!HasData)
            {
                compiledToolTip = null;
                return false;
            }

            string finalTooltip = Name + "\n";

            foreach (SinglePropertyData thisItem in singlePropertyData)
            {
                bool foundMatch = false;
                foreach (SinglePropertyData secondItem in comparedTo.singlePropertyData)
                {
                    if (string.Equals(thisItem.Name, secondItem.Name, StringComparison.InvariantCultureIgnoreCase))
                    {
                        foundMatch = true;
                        finalTooltip += thisItem.Name;

                        if (thisItem.FirstValue != double.MinValue && secondItem.FirstValue != double.MinValue)
                        {
                            double diff = thisItem.FirstValue - secondItem.FirstValue;
                            finalTooltip += $" {thisItem.FirstValue}";
                            if (diff != 0)
                            {
                                finalTooltip += $"({(diff >= 0 ? "/c[green]+" : "/c[red]")} {diff}/cd)";
                            }
                        }

                        if (thisItem.SecondValue > double.MinValue && secondItem.SecondValue > double.MinValue)
                        {
                            double diff = thisItem.SecondValue - secondItem.SecondValue;
                            finalTooltip += $" {thisItem.SecondValue}";
                            if (diff != 0)
                            {
                                finalTooltip += $"({(diff >= 0 ? "/c[green]+" : "/c[red]")}{diff}/cd)";
                            }
                        }

                        finalTooltip += "\n";
                        break;
                    }
                }
                if (!foundMatch)
                    finalTooltip += thisItem.ToString() + "\n";
            }

            compiledToolTip = finalTooltip;
            return true;
        }

        public string CompileTooltip()
        {
            string result = "";

            result += Name + "\n";
            foreach (SinglePropertyData data in singlePropertyData)
                result += $"{data.Name} [{data.FirstValue}] [{data.SecondValue}]\n";

            return result;
        }

        public class SinglePropertyData
        {
            public string OriginalString;
            public string Name = "";
            public double FirstValue = double.MinValue;
            public double SecondValue = double.MinValue;
            public double FirstDiff = 0;
            public double SecondDiff = 0;

            public SinglePropertyData(string line)
            {
                OriginalString = line;

                // Remove any color tags like /c[#...]
                string cleaned = RegexHelper.GetRegex(@"/c\[[#a-zA-Z0-9]+\]", RegexOptions.IgnoreCase).Replace(line, "").Replace("/cd", "").Trim();

                // Extract numbers
                MatchCollection matches = RegexHelper.GetRegex(@"-?\d+(\.\d+)?").Matches(cleaned);

                if (matches.Count > 0)
                {
                    double.TryParse(matches[0].Value, out FirstValue);
                    if (matches.Count > 1)
                        double.TryParse(matches[1].Value, out SecondValue);
                }

                // Remove all numbers and symbols from the cleaned string to isolate the name
                Name = RegexHelper.GetRegex(@"[-+]?\d+(\.\d+)?[%]?([- ]*\d+)?", RegexOptions.IgnoreCase).Replace(cleaned, "").Trim();

                // Fallback if something went wrong
                if (string.IsNullOrWhiteSpace(Name))
                    Name = line;
            }

            public override string ToString()
            {
                string output = "";

                if (Name != null)
                    output += Name;

                if (FirstValue != double.MinValue)
                    output += $" {FirstValue}";

                if (SecondValue != double.MinValue)
                    output += $" {SecondValue}";

                return output;
            }
        }
    }
}
