using C3.ModKit;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unfoundry;

namespace InventorySortOrder
{
    [UnfoundryMod(GUID)]
    public class Plugin : UnfoundryPlugin
    {
        public const string
            MODNAME = "InventorySortOrder",
            AUTHOR = "erkle64",
            GUID = AUTHOR + "." + MODNAME,
            VERSION = "0.1.0";

        public static LogSource log;

        public Plugin()
        {
            log = new LogSource(MODNAME);
        }

        public override void Load(Mod mod)
        {
            log.Log($"Loading {MODNAME}");
        }

        private struct ItemSlot {
            public uint slotId;
            public int sortIndex;
            public string name;
        }

        private static Dictionary<ulong, int> _itemSortOrders = new Dictionary<ulong, int>();
        private static int _nextSortIndex;

        [HarmonyPatch]
        public static class Patch
        {
            private static FieldInfo recipeCategory = typeof(CraftingCategoryContainer).GetField("recipeCategory", BindingFlags.NonPublic | BindingFlags.Instance);

            [HarmonyPatch(typeof(InventoryFrame), nameof(InventoryFrame.Init))]
            [HarmonyPrefix]
            public static void InventoryFrame_Init()
            {
                _itemSortOrders.Clear();
                _nextSortIndex = 0;
            }

            [HarmonyPatch(typeof(CraftingCategoryContainer), "createRecipeRowEntries")]
            [HarmonyPostfix]
            public static void CraftingCategoryContainer_createRecipeRowEntries(CraftingCategoryContainer __instance, bool isPersonal)
            {
                if (!isPersonal) return;

                var category = recipeCategory.GetValue(__instance) as CraftingRecipeCategory;

                foreach (var row in __instance.list_rows)
                {
                    foreach (var slot in row.list_slots)
                    {
                        var recipe = slot.recipe;
                        foreach (var outputItem in recipe.output)
                        {
                            if (outputItem.itemTemplate != null)
                            {
                                var item = outputItem.itemTemplate;
                                if (_itemSortOrders.TryAdd(item.id, _nextSortIndex)) _nextSortIndex++;
                                break;
                            }
                        }
                    }
                }
            }

            [HarmonyPatch(typeof(InventorySortEvent), nameof(InventorySortEvent.processEvent))]
            [HarmonyPostfix]
            public static void InventorySortEvent_processEvent(InventorySortEvent __instance)
            {
                var clientCharacter = GameRoot.getClientCharacter();
                var inventoryId = __instance.inventoryId;
                if (clientCharacter == null || clientCharacter.inventoryId != inventoryId) return;

                var inventoryPtr = InventoryManager.inventoryManager_getInventoryPtr(inventoryId);
                var slotCount = InventoryManager.inventoryManager_getInventorySlotCountByPtr(inventoryPtr);
                var slots = new ItemSlot[slotCount];
                log.Log($"Slot count: {slotCount}");
                var runningTemplateIdx = GameRoot.RunningIdxTable_itemTemplates_all;
                uint lastOccupiedSlot = 0;
                for (uint slotId = 0; slotId < slotCount; slotId++)
                {
                    ushort itemTemplateRunningIdx = 0;
                    uint itemCount = 0;
                    ushort lockedTemplateRunningIdx = 0;
                    IOBool isLocked = IOBool.iofalse;
                    InventoryManager.inventoryManager_getSingleSlotDataByPtr(inventoryPtr, slotId, ref itemTemplateRunningIdx, ref itemCount, ref lockedTemplateRunningIdx, ref isLocked, IOBool.iofalse);
                    log.Log($"Slot {slotId}: itemTemplateRunningIdx: {itemTemplateRunningIdx}, itemCount: {itemCount}, lockedTemplateRunningIdx: {lockedTemplateRunningIdx}, isLocked: {isLocked}");
                    if (itemCount > 0)
                    {
                        var itemTemplate = runningTemplateIdx.getDataByRunningIdx(itemTemplateRunningIdx);
                        if (itemTemplate == null || !_itemSortOrders.TryGetValue(itemTemplate.id, out var sortIndex)) sortIndex = int.MaxValue;
                        slots[slotId] = new ItemSlot
                        {
                            slotId = slotId,
                            sortIndex = sortIndex,
                            name = itemTemplate?.name ?? "zzz"
                        };
                        lastOccupiedSlot = slotId;
                    }
                }
                log.Log($"Last occupied slot: {lastOccupiedSlot}");

                Array.Sort(slots, (a, b) =>
                {
                    if (a.name == null) return (b.name == null) ? 0 : 1;
                    if (b.name == null) return -1;
                    if (a.sortIndex != b.sortIndex) return a.sortIndex.CompareTo(b.sortIndex);
                    return a.name.CompareTo(b.name);
                });
                log.Log($"Sorted slots:\n{string.Join("\n", slots.Select(x => $"{x.slotId} {x.sortIndex} {x.name}"))}");

                var shiftMap = new uint[lastOccupiedSlot + 1];
                for (uint slotId = 0; slotId <= lastOccupiedSlot; slotId++)
                {
                    shiftMap[slots[slotId].slotId] = slotId;
                }

                ActionManager.AddQueuedEvent(() =>
                {
                    for (uint slotId = 0; slotId <= lastOccupiedSlot; slotId++)
                    {
                        log.Log($"Sorting slot {slotId}");
                        while (shiftMap[slotId] != slotId)
                        {
                            log.Log($"Moving slot {slotId} to {shiftMap[slotId]}");
                            GameRoot.addLockstepEvent(new ItemMoveEvent(clientCharacter, inventoryId, slotId, inventoryId, shiftMap[slotId]));
                            var temp = shiftMap[slotId];
                            shiftMap[slotId] = shiftMap[temp];
                            shiftMap[temp] = temp;
                        }
                    }
                });
            }
        }
    }
}
