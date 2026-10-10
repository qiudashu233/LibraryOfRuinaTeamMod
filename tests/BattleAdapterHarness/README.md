This harness links the production `NativeBattleStateAdapter.cs` and calls its
real `ValidateManifest` method. The book/stage/card XML APIs use in-memory data
dictionaries. All native runtime APIs throw if touched; no Unity, Steam, battle
initialization, inventory or save operation is executed.

Run the current production regression:

```
dotnet run --project tests/BattleAdapterHarness/BattleAdapterHarness.csproj --configuration Release
```

An optional `-p:BaselineSource=<absolute source path>` can test an unchanged
historical adapter without putting a duplicate production source into Git. The
v17 regression snapshot is in ignored `artifacts/stage4a-v18/baseline-adapter.cs`
and has SHA256 `92969E837ECC2455DD8FC94C101A6F5C766DD4B8517F6D62C3758F6D1CB9A8A6`.

The key regression goes through production `BattleManifestCodec.FromPreparation`
using the real preparation shape. `NativePreparation` reads
`UnitDataModel.GetDeckCardModelAll`; the original game method calls `GetDeckAll`,
which sorts the current deck using `SortUtil.CardInfoCompByCost`. Thus the
supported enemy cards arrive as `[1,1,2,2,3,3]`, while the XML insertion order is
`[1,2,3,1,2,3]`. Both contain identical cards with identical multiplicities.
Validation must accept both without changing the supplied order, and reject
wrong multiplicities, missing/extra cards, unsupported identities, passives,
books, scripts and catalogue mismatches.
