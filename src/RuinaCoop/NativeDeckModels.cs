using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaCoop
{
    // These models exist solely for the native UI. They are never inserted into
    // LibraryModel, BookInventoryModel, InventoryModel or any save DTO.
    internal sealed class NativeDeckModels : IDisposable
    {
        // A retired view may still be referenced by a Unity callback after failed
        // cleanup. Weak marks keep that object guarded without retaining it.
        private static readonly ConditionalWeakTable<UnitDataModel, object> MirrorUnits = new ConditionalWeakTable<UnitDataModel, object>();
        private static readonly ConditionalWeakTable<BookModel, object> MirrorBooks = new ConditionalWeakTable<BookModel, object>();
        private static readonly ConditionalWeakTable<DeckModel, object> MirrorDecks = new ConditionalWeakTable<DeckModel, object>();
        private static readonly object MirrorMark = new object();
        [ThreadStatic] private static int _constructionDepth;
        internal static bool IsConstructingMirrors { get { return _constructionDepth != 0; } }
        internal readonly List<UnitDataModel> Units = new List<UnitDataModel>();
        internal readonly List<DiceCardItemModel> Stock = new List<DiceCardItemModel>();
        private readonly List<BookModel> _books = new List<BookModel>();
        private readonly List<DeckModel> _decks = new List<DeckModel>();
        private readonly List<int> _bookIds = new List<int>();
        private readonly List<int> _customBookIds = new List<int>();
        private bool _disposed;

        internal static bool IsMirrorUnit(UnitDataModel unit) { object mark; return unit != null && MirrorUnits.TryGetValue(unit, out mark); }
        internal static bool IsMirrorBook(BookModel book) { object mark; return book != null && MirrorBooks.TryGetValue(book, out mark); }
        internal static bool IsMirrorDeck(DeckModel deck) { object mark; return deck != null && MirrorDecks.TryGetValue(deck, out mark); }

        internal NativeDeckModels(ProgressSnapshot snapshot)
        {
            try
            {
                var floor = snapshot.Floors.Find(entry => (byte)entry.Sephirah == snapshot.SelectedFloorId);
                if (floor == null || floor.Units.Count != snapshot.UnitDecks.Count)
                    throw new InvalidOperationException("Host roster is unavailable.");
                for (var i = 0; i < snapshot.UnitDecks.Count; i++)
                {
                    var entry = snapshot.UnitDecks[i];
                    var defaultId = entry.Display.AppearanceAvailable ? entry.Display.DefaultBookId : entry.BookId;
                    if (defaultId <= 0 || entry.BookId <= 0)
                        throw new InvalidOperationException("This host roster contains an unsupported key page.");
                    // The vanilla constructor creates private default-book, gift
                    // and customizing objects; it does not register inventory.
                    // Guest guards can suppress its EquipBook/CreateDeck calls:
                    // authoritative display fields are set directly below.
                    UnitDataModel unit;
                    _constructionDepth++;
                    try { unit = new UnitDataModel(defaultId, floor.Sephirah, entry.Display.IsSephirah); }
                    finally { _constructionDepth--; }
                    Units.Add(unit);
                    MirrorUnits.Add(unit, MirrorMark);
                    RegisterBook(unit.defaultBook);
                    var book = CreateBook(entry.BookId);
                    _bookIds.Add(entry.BookId);
                    _customBookIds.Add(entry.Display.AppearanceAvailable ? entry.Display.CustomBookId : 0);
                    NativeUi.Set(unit, "_bookItem", book);
                    book.owner = unit;
                    if (entry.Display.AppearanceAvailable && entry.Display.CustomBookId > 0)
                        NativeUi.Set(unit, "_CustomBookItem", CreateBook(entry.Display.CustomBookId));
                }
                Update(snapshot);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private BookModel CreateBook(int id)
        {
            var xml = BookXmlList.Instance.GetData(id);
            if (xml == null) throw new InvalidOperationException("Unknown vanilla key page: " + id + ".");
            var book = new BookModel(xml);
            RegisterBook(book);
            return book;
        }

        private void RegisterBook(BookModel book)
        {
            if (book == null || _books.Contains(book)) return;
            _books.Add(book);
            MirrorBooks.Add(book, MirrorMark);
            foreach (var deck in book.GetDeckAll_nocopy())
            {
                if (deck == null || _decks.Contains(deck)) continue;
                _decks.Add(deck);
                MirrorDecks.Add(deck, MirrorMark);
            }
            var current = NativeUi.Get(book, "_deck") as DeckModel;
            if (current != null && !_decks.Contains(current))
            {
                _decks.Add(current);
                MirrorDecks.Add(current, MirrorMark);
            }
        }

        internal void Update(ProgressSnapshot snapshot)
        {
            if (_disposed || Units.Count != snapshot.UnitDecks.Count)
                throw new InvalidOperationException("Host roster changed.");
            var floor = snapshot.Floors.Find(entry => (byte)entry.Sephirah == snapshot.SelectedFloorId);
            for (var i = 0; i < Units.Count; i++)
            {
                var unit = Units[i];
                var entry = snapshot.UnitDecks[i];
                var display = entry.Display;
                if (!display.AppearanceAvailable) unit.textureIndex = -1;
                if (_bookIds[i] != entry.BookId)
                {
                    var replacement = CreateBook(entry.BookId);
                    replacement.owner = unit;
                    NativeUi.Set(unit, "_bookItem", replacement);
                    _bookIds[i] = entry.BookId;
                }
                var customId = display.AppearanceAvailable ? display.CustomBookId : 0;
                if (_customBookIds[i] != customId)
                {
                    NativeUi.Set(unit, "_CustomBookItem", customId > 0 ? CreateBook(customId) : null);
                    _customBookIds[i] = customId;
                }
                NativeUi.Set(unit, "_name", floor.Units[i]);
                NativeUi.Set(unit, "_tempName", floor.Units[i]);
                var book = unit.bookItem;
                book.instanceId = entry.BookInstanceId;
                var deck = NativeUi.Get(book, "_deck") as DeckModel;
                var cards = deck.GetCardList_nocopy();
                cards.Clear();
                foreach (var id in entry.Cards) cards.Add(CardXml(id));
                var passives = new List<PassiveModel>();
                if (display.Available)
                {
                    book.SetHp(display.MaxHp);
                    book.SetBp(display.Break);
                    foreach (var id in display.PassiveIds)
                        passives.Add(new PassiveModel(new LorId(id), entry.BookInstanceId, 0));
                }
                NativeUi.Set(book, "_activatedAllPassives", passives);
                if (display.AppearanceAvailable)
                {
                    unit.gender = (Gender)display.Gender;
                    unit.appearanceType = (Gender)display.AppearanceType;
                    unit.isSephirah = display.IsSephirah;
                    NativeUi.Set(unit.CustomBookItem, "_characterSkin", display.CharacterSkin ?? "");
                    var custom = unit.customizeData;
                    NativeUi.Set(custom, "_bUseCustomData", display.UseCustom);
                    custom.specialCustomID = new LorId(display.SpecialCustomId);
                    custom.frontHairID = display.FrontHair;
                    custom.backHairID = display.BackHair;
                    custom.eyeID = display.Eye;
                    custom.browID = display.Brow;
                    custom.mouthID = display.Mouth;
                    custom.headID = display.Head;
                    custom.hairColor = Unpack(display.HairColor);
                    custom.eyeColor = Unpack(display.EyeColor);
                    custom.skinColor = Unpack(display.SkinColor);
                    NativeUi.Set(custom, "_height", display.Height);
                }
            }
            Stock.Clear();
            foreach (var entry in snapshot.CardStock)
                Stock.Add(new DiceCardItemModel(CardXml(entry.Id)) { num = entry.Count });
        }

        private static DiceCardXmlInfo CardXml(int id)
        {
            var xml = ItemXmlDataList.instance.GetCardItem(new LorId(id), false);
            if (xml == null || !xml.id.IsBasic())
                throw new InvalidOperationException("Unknown vanilla combat page: " + id + ".");
            return xml;
        }

        private static Color Unpack(uint rgba)
        {
            return new Color(((rgba >> 24) & 255) / 255f, ((rgba >> 16) & 255) / 255f,
                ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Units.Clear();
            Stock.Clear();
            _books.Clear();
            _decks.Clear();
        }
    }

    // Reflection keeps UI-only dependencies out of the compiled reference set.
    // Every required target is validated against the supported game at install.
    internal static class NativeUi
    {
        internal static object Get(object target, string name)
        {
            if (target == null) return null;
            var field = HarmonyLib.AccessTools.Field(target.GetType(), name);
            if (field != null) return field.GetValue(target);
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null) return property.GetValue(target, null);
            throw new MissingMemberException(target.GetType().FullName, name);
        }

        internal static void Set(object target, string name, object value)
        {
            if (target == null) throw new InvalidOperationException("Native UI object is unavailable: " + name);
            var field = HarmonyLib.AccessTools.Field(target.GetType(), name);
            if (field != null)
            {
                if (value != null && field.FieldType.IsEnum && !field.FieldType.IsInstanceOfType(value))
                    value = Enum.ToObject(field.FieldType, value);
                field.SetValue(target, value);
                return;
            }
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null) { property.SetValue(target, value, null); return; }
            throw new MissingMemberException(target.GetType().FullName, name);
        }

        internal static object Call(object target, string name, params object[] args)
        {
            if (target == null) throw new InvalidOperationException("Native UI target is unavailable: " + name);
            foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != name) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != args.Length) continue;
                var matches = true;
                for (var i = 0; i < args.Length; i++)
                    if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i])) matches = false;
                if (matches) return method.Invoke(target, args);
            }
            throw new MissingMethodException(target.GetType().FullName, name);
        }

        internal static object Singleton(string typeName)
        {
            var type = typeof(UnitDataModel).Assembly.GetType(typeName, true);
            while (type != null)
            {
                foreach (var name in new[] { "Instance", "Manager", "instance" })
                {
                    var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (property != null) return property.GetValue(null, null);
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (field != null) return field.GetValue(null);
                }
                type = type.BaseType;
            }
            throw new MissingMemberException(typeName, "Instance");
        }

        internal static void Active(object component, bool active)
        {
            if (component == null) return;
            var gameObject = component as GameObject ?? Get(component, "gameObject") as GameObject;
            if (gameObject != null) gameObject.SetActive(active);
        }
    }
}
