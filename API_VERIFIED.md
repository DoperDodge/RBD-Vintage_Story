# API_VERIFIED.md

Every API this mod depends on, checked against the **real** assemblies of
**Vintage Story 1.22.7** (`vs_server_linux-x64_1.22.7.tar.gz`, SHA of the
downloaded archive recorded in CI). Signatures below were extracted by
reflection (`tools/ApiDump`) and, where behaviour mattered, by decompiling the
shipped IL (`tools/Decomp`). Nothing here is from memory.

> **How to re-verify after a game update**
> ```bash
> dotnet tools/ApiDump/bin/Release/net10.0/ApiDump.dll /opt/vs /tmp/api_full.txt
> apix "<TypeRegex>" "<memberRegex>"     # helper installed by scripts/devenv.sh
> vsdec survival Vintagestory.GameContent.SomeType SomeMember
> ```

---

## 0. Corrections to PLAN.md

The plan was written from memory and got several things wrong. These are the
deviations, recorded here as §0 of the plan demands.

| PLAN.md said | Reality in 1.22.7 | What the mod does |
|---|---|---|
| ".NET 7, maybe 8" (§2.1) | `VintagestoryServer.runtimeconfig.json` → **`net10.0`** | `<TargetFramework>net10.0</TargetFramework>` |
| `dependencies: { "game": "1.21.0" }` (§2.3) | current stable is **1.22.7** | `"game": "1.22.0"` |
| Reference `VSCreativeMod.dll` | not needed by anything we call | dropped from the reference list |
| "verify the SERVER-side method to set time backwards" (§17) | `IGameCalendar.Add(float)` is `timespan = timespan.Add(TimeSpan.FromHours(h))` — **negative values work**; `ISaveGame.TotalGameSeconds` is a settable `long` | rewind uses `Calendar.Add(-delta)` (interface-only, no lib cast) and re-asserts `SaveGame.TotalGameSeconds` for persistence |
| `entity.State = EnumEntityState.Inactive` to freeze AI | works but the entity then stops ticking *and* stops being synced; `AiTaskManager.OnShouldExecuteTask` is a cleaner veto | freeze vetoes task execution via that event and zeroes `ServerPos.Motion` |
| "OnEntityReceiveDamage(DamageSource, ref float)" | **correct**, exactly as written | used as-is |
| "Harmony prefix on `EntityPlayer.Die`" | `Entity.Die(EnumDespawnReason = Death, DamageSource = null)` is `virtual` on `Entity`; `EntityPlayer` does not redeclare it | prefix targets `Entity.Die`, filtered to `EntityPlayer` at runtime |
| `sapi.Event.OnEntitySpawn` etc. live on `IServerEventAPI` | they live on the **base** `IEventAPI` | subscribed via `sapi.Event` all the same (inherited) |

---

## 1. Mod lifecycle

```csharp
// VintagestoryAPI.dll — Vintagestory.API.Common.ModSystem
virtual void  Start(ICoreAPI api)
virtual void  StartPre(ICoreAPI api)
virtual void  StartClientSide(ICoreClientAPI api)
virtual void  StartServerSide(ICoreServerAPI api)
virtual void  AssetsLoaded(ICoreAPI api)
virtual void  AssetsFinalize(ICoreAPI api)
virtual double ExecuteOrder()
virtual bool  ShouldLoad(EnumAppSide forSide)
virtual void  Dispose()
```

## 2. Server events (`sapi.Event`)

On `IServerEventAPI`:
```
event PlayerDelegate       PlayerJoin, PlayerNowPlaying, PlayerLeave,
                           PlayerDisconnect, PlayerRespawn, PlayerCreate, PlayerReady
event PlayerDeathDelegate  PlayerDeath          // (IServerPlayer, DamageSource)
event PlayerChatDelegate   PlayerChat           // (IServerPlayer, int channelId,
                                                //  ref string message, ref string data,
                                                //  BoolRef consumed)     <-- taboo hook
event BlockBrokenDelegate  DidBreakBlock        // (IServerPlayer, int oldblockId, BlockSelection)
event BlockBreakDelegate   BreakBlock           // (IServerPlayer, BlockSelection,
                                                //  ref float dropQuantityMultiplier,
                                                //  ref EnumHandling)     <-- pre-break, BE still alive
event BlockPlacedDelegate  DidPlaceBlock        // (IServerPlayer, int oldblockId, BlockSelection, ItemStack)
event BlockUsedDelegate    DidUseBlock          // (IServerPlayer, BlockSelection)
event Action               SaveGameLoaded, GameWorldSave, SaveGameCreated
event ChunkColumnLoadedDelegate / ChunkColumnUnloadDelegate
```

Inherited from `IEventAPI`:
```
event EntityDelegate        OnEntitySpawn, OnEntityLoaded     // (Entity)
event EntityDeathDelegate   OnEntityDeath                     // (Entity, DamageSource)
event EntityDespawnDelegate OnEntityDespawn                   // (Entity, EntityDespawnData)
void   EnqueueMainThreadTask(Action action, string code)
long   RegisterGameTickListener(Action<float> onGameTick, int millisecondInterval, int initialDelayOffsetMs = 0)
long   RegisterCallback(Action<float> onTimePassed, int millisecondDelay)
```

**`PlayerChat` is the taboo interception point**: `message` is `ref` and
`consumed` is a `BoolRef`, so a message can be rewritten *or eaten entirely*
before any client sees it. This is what makes §7.3's "the chat message is never
sent" implementable exactly as specified.

## 3. Persistence

```csharp
// Vintagestory.API.Server.ISaveGame   (sapi.WorldManager.SaveGame)
string SavegameIdentifier { get; }          // stable per-world id -> ModData folder name
long   TotalGameSeconds   { get; set; }     // settable: authoritative world clock
byte[] GetData(string key);  T GetData<T>(string key, T def = null)
void   StoreData(string key, byte[] data);  void StoreData<T>(string key, T data)

// ICoreAPI
string GetOrCreateDataPath(string foldername)
T      LoadModConfig<T>(string filename)
void   StoreModConfig<T>(T data, string filename)
```

## 4. Blocks / block entities (rewind targets)

```csharp
// IBlockAccessor
Block       GetBlock(BlockPos pos);            Block GetBlock(int blockId)
void        SetBlock(int blockId, BlockPos pos)
BlockEntity GetBlockEntity(BlockPos pos);      T GetBlockEntity<T>(BlockPos pos)
void        SpawnBlockEntity(string classname, BlockPos pos, ItemStack byItemStack = null)
void        SpawnBlockEntity(BlockEntity be)
void        RemoveBlockEntity(BlockPos pos)
void        MarkBlockDirty(BlockPos pos, IPlayer skipPlayer = null)
void        MarkBlockEntityDirty(BlockPos pos)
void        TriggerNeighbourBlockUpdate(BlockPos pos)
List<BlockUpdate> Commit()                     // on IBulkBlockAccessor too

// IWorldManagerAPI
IBulkBlockAccessor GetBlockAccessorBulkUpdate(bool synchronize, bool relight, bool debug = false)
void LoadChunkColumnPriority(int cx, int cz, ChunkLoadOptions options = null)
void FullRelight(BlockPos min, BlockPos max, bool sendToClients)
IServerChunk[] BlockingLoadChunkColumn(int cx, int cz)

// BlockEntity
void ToTreeAttributes(ITreeAttribute tree)
void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolve)
void Initialize(ICoreAPI api);  void CreateBehaviors(Block, IWorldAccessor)
void MarkDirty(bool redrawOnClient = false, IPlayer skipPlayer = null)
BlockPos Pos;

// IClassRegistryAPI (world.ClassRegistry)
BlockEntity CreateBlockEntity(string blockEntityClass)
Entity      CreateEntity(string entityClass);  Entity CreateEntity(EntityProperties type)
string      GetBlockEntityClass(Type type)

// Vintagestory.API.Datastructures.TreeAttribute
byte[] ToBytes();  void FromBytes(byte[] data);  static TreeAttribute CreateFromBytes(byte[])
```

`ChunkLoadOptions` carries the completion callback used by the rewind preload
step — verified member: `OnLoaded` (`Action`).

## 5. Entities

```csharp
// Vintagestory.API.Common.Entities.Entity
virtual void Die(EnumDespawnReason reason = EnumDespawnReason.Death,
                 DamageSource damageSourceForDeath = null)      // <-- Harmony prefix target
void ToBytes(BinaryWriter writer, bool forClient)
void FromBytes(BinaryReader reader, bool isSync)
void TeleportTo(EntityPos position, Action onTeleported = null)
void TeleportToDouble(double x, double y, double z, Action onTeleported = null)
void AddBehavior(EntityBehavior);  T GetBehavior<T>();  EntityBehavior GetBehavior(string)
SyncedTreeAttribute WatchedAttributes;  ITreeAttribute Attributes
EntityPos ServerPos, Pos, SidedPos;  long EntityId;  bool Alive;  EnumEntityState State

// Vintagestory.API.Common.Entities.EntityBehavior
virtual void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)   // <-- death intercept
virtual void OnGameTick(float deltaTime)
virtual void Initialize(EntityProperties properties, JsonObject attributes)
virtual void OnEntityDeath(DamageSource);  virtual void OnEntityDespawn(EntityDespawnData)
virtual string PropertyName()                                   // MUST be overridden
Entity entity;                                                  // protected field

// IWorldAccessor
void   SpawnEntity(Entity entity)
Entity SpawnItemEntity(ItemStack stack, Vec3d position, Vec3d velocity = null)
Entity GetEntityById(long id);  Entity[] GetEntitiesAround(Vec3d, float h, float v, ActionBoolReturn<Entity>)
```

`EnumDespawnReason`: `Death=0, Combusted=1, OutOfRange=2, PickedUp=3, Unload=4,
Disconnect=5, Expire=6, Removed=7`.
Rewind despawns with `Removed` so no drops and no death events fire.

## 6. Damage classification

```csharp
// DamageSource (fields, not properties)
EnumDamageSource Source;  EnumDamageType Type;  Entity SourceEntity, CauseEntity;
Block SourceBlock;  Vec3d SourcePos, HitPosition;  int DamageTier;

EnumDamageSource: Block=0 Player=1 Fall=2 Drown=3 Revive=4 Void=5 Suicide=6
                  Internal=7 Entity=8 Explosion=9 Machine=10 Unknown=11 Weather=12 Bleed=13
EnumDamageType:   Gravity=0 Fire=1 BluntAttack=2 SlashingAttack=3 PiercingAttack=4
                  Suffocation=5 Heal=6 Poison=7 Hunger=8 Crushing=9 Frost=10
                  Electricity=11 Heat=12 Injury=13 Acid=14
```

`DeathCause` in §5.3 of the plan maps onto these pairs; see `Death/DeathCause.cs`.

## 7. Health

```csharp
// VSEssentials.dll — Vintagestory.GameContent.EntityBehaviorHealth
float Health { get; set; }        float MaxHealth { get; }
float BaseMaxHealth { get; set; }
void  SetMaxHealthModifiers(string key, float value)   // <-- phantom pain uses this
void  UpdateMaxHealth()
Dictionary<string,float> MaxHealthModifiers { get; set; }
```

Phantom pain therefore does **not** touch `BaseMaxHealth` (which would persist
into the save); it registers a named modifier and removes it on decay.

## 8. Player inventory

```csharp
// IPlayerInventoryManager  (IServerPlayer.InventoryManager)
Dictionary<string, IInventory> Inventories { get; }   // every inventory, keyed by id
IEnumerable<InventoryBase> InventoriesOrdered { get; }
IInventory GetOwnInventory(string inventoryClassName)

// InventoryBase
void ToTreeAttributes(ITreeAttribute tree)
void FromTreeAttributes(ITreeAttribute tree)
void Clear()
string InventoryID { get; }   string ClassName { get; }
```

Snapshotting walks `Inventories`, writes each into a sub-tree keyed by
`InventoryID`, and skips creative//mouse-cursor inventories by class name.

## 9. Networking

```csharp
// IServerNetworkChannel                      // IClientNetworkChannel
IServerNetworkChannel RegisterMessageType<T>()  IClientNetworkChannel RegisterMessageType<T>()
IServerNetworkChannel SetMessageHandler<T>(NetworkClientMessageHandler<T>)
void SendPacket<T>(T message, params IServerPlayer[] players)
void BroadcastPacket<T>(T message, params IServerPlayer[] exceptPlayers)
                                                void SendPacket<T>(T message)
                                                IClientNetworkChannel SetMessageHandler<T>(NetworkServerMessageHandler<T>)
                                                bool Connected { get; }
sapi.Network.RegisterChannel(string name);  capi.Network.RegisterChannel(string name)
```
Message types must be `[ProtoContract]` classes (protobuf-net ships in `Lib/`).

## 10. Commands (fluent builder, 1.19+)

```csharp
sapi.ChatCommands.Create("rbd")
    .WithDescription("...")
    .RequiresPrivilege(Privilege.chat)
    .RequiresPlayer()
    .BeginSubCommand("status").HandleWith(OnStatus).EndSubCommand()
    .BeginSubCommand("miasma")
        .WithArgs(sapi.ChatCommands.Parsers.OptionalInt("value"))
        .HandleWith(OnMiasma)
    .EndSubCommand();
// handler: TextCommandResult OnStatus(TextCommandCallingArgs args)
// results:  TextCommandResult.Success(msg) / .Error(msg)
// caller:   args.Caller.Player as IServerPlayer
```

## 11. Client rendering

```csharp
capi.Event.RegisterRenderer(IRenderer r, EnumRenderStage stage, string profilingName = null)
capi.Event.UnregisterRenderer(IRenderer r, EnumRenderStage stage)

// IRenderer
double RenderOrder { get; }   int RenderRange { get; }
void OnRenderFrame(float deltaTime, EnumRenderStage stage)

EnumRenderStage: Before=0 Opaque=1 OIT=2 AfterOIT=3 ShadowFar=4 ShadowFarDone=5
                 ShadowNear=6 ShadowNearDone=7 AfterPostProcessing=8 AfterBlit=9
                 Ortho=10 AfterFinalComposition=11 Done=12
```

**`Ortho` is the stage the mod's full-screen effects use** — at `Ortho` the
2D orthographic projection is already set up, so `Render2DTexture` lands in
screen space. `AfterPostProcessing` is used only by the Void, which must cover
the world *before* the GUI is drawn.

```csharp
// IRenderAPI
void Render2DTexture(int textureid, float x, float y, float w, float h, float z = 50, Vec4f color = null)
void Render2DTexturePremultipliedAlpha(int textureid, double x, double y, double w, double h, float z = 50, Vec4f color = null)
void RenderMesh(MeshRef meshRef);   MeshRef UploadMesh(MeshData data)
void GlToggleBlend(bool blend, EnumBlendMode blendMode = EnumBlendMode.Standard)
IShaderProgram CurrentActiveShader { get; }
IShaderProgram GetEngineShader(EnumShaderProgram program)
List<FrameBufferRef> FrameBuffers { get; }

// IShaderAPI
IShaderProgram NewShaderProgram()
int  RegisterFileShaderProgram(string name, IShaderProgram program)
int  RegisterMemoryShaderProgram(string name, IShaderProgram program)
bool ReloadShaders()
capi.Event.ReloadShader   // event ActionBoolReturn — re-register on shader reload
```

## 12. Input / hotkeys

```csharp
capi.Input.RegisterHotKey(string code, string name, GlKeys key, HotkeyType type = HotkeyType.CharacterControls,
                          bool altPressed = false, bool ctrlPressed = false, bool shiftPressed = false)
capi.Input.SetHotKeyHandler(string code, ActionConsumable<KeyCombination> handler)
OrderedDictionary<string, HotKey> HotKeys { get; }
```

## 13. Sound

```csharp
world.PlaySoundAt(AssetLocation loc, double x, double y, double z, IPlayer dualCallByPlayer,
                  EnumSoundType soundType, float pitch, float range = 32, float volume = 1)
world.PlaySoundAt(AssetLocation loc, IPlayer atPlayer, IPlayer dualCallByPlayer = null,
                  bool randomizePitch = true, float range = 32, float volume = 1)
capi.World.LoadSound(SoundParams)  ->  ILoadedSound { Start/Stop/FadeOut/SetVolume/SetPitch }
```
`ILoadedSound` is what `AudioDirector` uses for the ducking and the sustained
heartbeat/whisper beds; one-shots go through `PlaySoundAt`.

## 14. Temporal integration (VSSurvivalMod)

```csharp
// Vintagestory.GameContent.SystemTemporalStability : ModSystem
float GetTemporalStability(BlockPos pos);  float GetTemporalStability(Vec3d pos)
float GetTemporalStability(double x, double y, double z)
event GetTemporalStabilityDelegate OnGetTemporalStability   // hook to depress stability locally
float StormStrength { get; }
TemporalStormRunTimeData StormData { get; }
```
Accessed with `sapi.ModLoader.GetModSystem<SystemTemporalStability>()`, always
null-checked so the mod still loads if VSSurvivalMod is absent or renamed.

## 15. AI (miasma aggro + freeze)

```csharp
// VSEssentials.dll
class EntityBehaviorTaskAI : EntityBehavior { AiTaskManager TaskManager; WaypointsTraverser PathTraverser; }
class AiTaskManager {
    List<IAiTask> AllTasks { get; }   IAiTask[] ActiveTasksBySlot { get; }
    event ActionBoolReturn<IAiTask> OnShouldExecuteTask;   // <-- freeze veto
    void ExecuteTask<T>();  T GetTask<T>();  IEnumerable<TTask> GetTasks<TTask>()
}
```
