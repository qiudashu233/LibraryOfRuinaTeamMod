using System;
using System.Linq;
using RuinaCoop;
using UI;

const ulong roomId = 109775243212511914UL;
var source = new ProgressSnapshot
{
    Sequence = 0x12345678,
    Chapter = 7,
    LibraryLevel = 60,
    SelectedStageId = 101
};
source.Stages.Add(new ProgressSnapshot.StageEntry
{
    Id = 101, Chapter = 7, State = StoryState.Clear, Name = "测试 무대 library"
});
source.Stages.Add(new ProgressSnapshot.StageEntry
{
    Id = 102, Chapter = 7, State = StoryState.Open, Name = "Second stage"
});
var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth, Level = 6 };
floor.Units.Add("罗兰");
floor.Units.Add("Angela 안젤라");
source.Floors.Add(floor);
for (var id = 103; id <= 235; id++)
{
    source.Stages.Add(new ProgressSnapshot.StageEntry
    {
        Id = id, Chapter = 7, State = StoryState.Open, Name = "接待 " + id + " - 테스트"
    });
}
for (var index = 1; index < 10; index++)
{
    var otherFloor = new ProgressSnapshot.FloorEntry
    {
        Sephirah = (SephirahType)index, Level = 6
    };
    for (var unit = 0; unit < 5; unit++)
    {
        otherFloor.Units.Add("Librarian " + index + "-" + unit + " 馆员");
    }
    source.Floors.Add(otherFloor);
}

var packet = source.Encode(roomId);
Check(ProgressSnapshot.TryDecode(packet, roomId, out var decoded), "round-trip decode");
Check(decoded.Sequence == source.Sequence && decoded.Chapter == 7 &&
      decoded.LibraryLevel == 60 && decoded.SelectedStageId == 101, "header fields");
Check(decoded.Stages.Count == 135 && decoded.Stages[0].Name == "测试 무대 library" &&
      decoded.Stages[1].State == StoryState.Open, "stage fields and Unicode");
Check(decoded.Floors.Count == 10 && decoded.Floors[0].Units.SequenceEqual(floor.Units),
    "floor and librarian fields");
Check(!ProgressSnapshot.TryDecode(packet, roomId + 1, out _), "room binding");
Check(!ProgressSnapshot.TryDecode(null, roomId, out _), "null packet");
Check(!ProgressSnapshot.TryDecode(new byte[65537], roomId, out _), "packet size limit");
Check(!ProgressSnapshot.TryDecode(packet.Take(packet.Length - 1).ToArray(), roomId, out _),
    "truncation");
Check(!ProgressSnapshot.TryDecode(packet.Concat(new byte[] { 0 }).ToArray(), roomId, out _),
    "trailing bytes");
var damaged = (byte[])packet.Clone();
damaged[0] ^= 0xff;
Check(!ProgressSnapshot.TryDecode(damaged, roomId, out _), "magic");
damaged = (byte[])packet.Clone();
damaged[4]++;
Check(!ProgressSnapshot.TryDecode(damaged, roomId, out _), "wire version");
damaged = (byte[])packet.Clone();
BitConverter.GetBytes(99999).CopyTo(damaged, 25);
Check(!ProgressSnapshot.TryDecode(damaged, roomId, out _), "selected stage must exist");
var rawInvalid = new ProgressSnapshot();
rawInvalid.Stages.Add(new ProgressSnapshot.StageEntry
    { Id = 101, State = StoryState.Open, Name = "First" });
rawInvalid.Stages.Add(new ProgressSnapshot.StageEntry
    { Id = 101, State = StoryState.Open, Name = "Duplicate" });
Check(!ProgressSnapshot.TryDecode(rawInvalid.Encode(roomId), roomId, out _, out var invalidReason) &&
      invalidReason.Contains("repeats ID 101"), "duplicate stage diagnostics");
var rawFloor = new LibraryFloorModel { Sephirah = SephirahType.Malkuth, Level = 6 };
rawFloor.Units.Add(new UnitData { name = "罗兰" });
LibraryModel.Instance.OpenedFloors.Add(rawFloor);
LibraryModel.Instance.OpenedFloors.Add(rawFloor);
StageClassInfoList.Instance.Stages.Add(new StageData
    { id = new StageId { id = 101 }, currentState = StoryState.Open, stageName = "First" });
StageClassInfoList.Instance.Stages.Add(new StageData
    { id = new StageId { id = 101 }, currentState = StoryState.Open, stageName = "Duplicate" });
StageClassInfoList.Instance.Stages.Add(new StageData
    { id = new StageId { id = 0 }, currentState = StoryState.Open, stageName = "Nonpositive" });
StageClassInfoList.Instance.Stages.Add(new StageData
    { id = new StageId { id = 102 }, currentState = StoryState.Close, stageName = "Closed" });
var captured = ProgressSnapshot.Capture(101);
Check(captured.Stages.Count == 1 && captured.Stages[0].Id == 101 && captured.Floors.Count == 1,
    "capture filters invalid or duplicate IDs");
Check(ProgressSnapshot.TryDecode(captured.Encode(roomId), roomId, out var capturedDecoded) &&
      capturedDecoded.Stages.Count == 1 && capturedDecoded.Floors.Count == 1,
    "captured snapshot passes receiver validation");
var claims = new PrepClaims();
claims.Reconcile(captured);
Check(claims.SelectFloor(captured, (byte)SephirahType.Malkuth), "host selects an opened floor");
captured = ProgressSnapshot.Capture(101);
claims.Reconcile(captured);
var firstRevision = claims.Revision;
const ulong guestOne = 76561199548728145UL;
const ulong guestTwo = 76561198377244747UL;
Check(claims.Apply(guestOne, 101, 0, 0, firstRevision, ClaimAction.Claim) ==
      ClaimResultCode.Accepted, "first claim wins");
Check(claims.Apply(guestTwo, 101, 0, 0, firstRevision, ClaimAction.Claim) ==
      ClaimResultCode.StaleRevision, "simultaneous stale claim rejected");
Check(claims.Apply(guestTwo, 101, 0, 0, claims.Revision, ClaimAction.Claim) ==
      ClaimResultCode.AlreadyClaimed, "claimed slot cannot be stolen");
Check(claims.Apply(guestTwo, 101, 0, 0, claims.Revision, ClaimAction.Release) ==
      ClaimResultCode.NotOwner, "other member cannot release claim");
captured = ProgressSnapshot.Capture(101);
claims.Reconcile(captured);
Check(captured.ClaimOwners.Count == 1 && captured.ClaimOwners[0] == guestOne &&
      ProgressSnapshot.TryDecode(captured.Encode(roomId), roomId, out var claimedDecoded) &&
      claimedDecoded.ClaimOwners[0] == guestOne, "claim mirrored in snapshot");
Check(claims.Apply(guestOne, 101, 0, 0, claims.Revision, ClaimAction.Release) ==
      ClaimResultCode.Accepted, "owner can release claim");
Check(claims.Apply(guestTwo, 101, 0, 0, claims.Revision, ClaimAction.Claim) ==
      ClaimResultCode.Accepted, "released slot can be claimed again");
Check(claims.ReleaseAbsent(id => id == guestOne), "host confirms departed member release");
captured = ProgressSnapshot.Capture(101);
claims.Reconcile(captured);
Check(captured.ClaimOwners[0] == 0, "departed member slot cleared");
Check(claims.Apply(guestOne, 101, 0, 0, claims.Revision, ClaimAction.Claim) ==
      ClaimResultCode.Accepted, "claim after departed member cleanup");
LibraryModel.Instance.OpenedFloors[0].Units[0] = new UnitData { name = "罗兰" };
captured = ProgressSnapshot.Capture(101);
claims.Reconcile(captured);
Check(captured.SelectedFloorId == PrepClaims.NoFloor && captured.ClaimOwners.Count == 0,
    "roster object change clears claims even when names match");
Check(claims.SelectFloor(captured, 0), "floor can be selected again");
captured = ProgressSnapshot.Capture(0);
claims.Reconcile(captured);
Check(captured.SelectedFloorId == PrepClaims.NoFloor && captured.ClaimOwners.Count == 0,
    "changing stage clears floor and claims");
var claimRequest = new ClaimRequest { RequestId = 4, StageId = 101, FloorId = 0,
    UnitIndex = 0, ExpectedRevision = 8, Action = ClaimAction.Claim };
var requestPacket = ClaimProtocol.EncodeRequest(roomId, claimRequest);
Check(ClaimProtocol.TryDecodeRequest(requestPacket, roomId, out var parsedRequest) &&
      parsedRequest.RequestId == 4 && parsedRequest.ExpectedRevision == 8,
    "claim request round trip");
Check(!ClaimProtocol.TryDecodeRequest(requestPacket, roomId + 1, out _) &&
      !ClaimProtocol.TryDecodeRequest(requestPacket.Take(27).ToArray(), roomId, out _),
    "claim request room binding and exact size");
var claimReply = new ClaimReply { RequestId = 4,
    Result = ClaimResultCode.AlreadyClaimed, Revision = 9 };
var replyPacket = ClaimProtocol.EncodeReply(roomId, claimReply);
Check(ClaimProtocol.TryDecodeReply(replyPacket, roomId, out var parsedReply) &&
      parsedReply.Result == ClaimResultCode.AlreadyClaimed && parsedReply.Revision == 9 &&
      !ClaimProtocol.TryDecodeReply(replyPacket, roomId + 1, out _),
    "claim reply round trip and room binding");
var challenge = RelayAuth.NewChallenge();
Check(challenge.Length == 32 && challenge != RelayAuth.NewChallenge(), "random challenge");
Check(RelayAuth.TryReadChallenge(RelayAuth.ChallengePacket(challenge), out var parsedChallenge) &&
      parsedChallenge == challenge, "challenge packet");
Check(RelayAuth.TryReadAccepted(RelayAuth.AcceptedPacket(challenge), out var acceptedChallenge) &&
      acceptedChallenge == challenge, "accepted packet");
var proof = RelayAuth.ProofMessage(challenge, 76561198377244747UL, roomId);
Check(RelayAuth.IsProofMessage(proof), "proof format");
Check(RelayAuth.MatchesProof(proof, challenge, 76561198377244747UL, roomId), "valid member proof");
Check(!RelayAuth.MatchesProof(proof, challenge, 76561198377244748UL, roomId),
    "proof bound to sender");
Check(!RelayAuth.MatchesProof(proof, challenge, 76561198377244747UL, roomId + 1),
    "proof bound to room");
Check(!RelayAuth.MatchesProof(proof, RelayAuth.NewChallenge(), 76561198377244747UL, roomId),
    "proof bound to socket challenge");
var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
try
{
    System.Globalization.CultureInfo.CurrentCulture =
        System.Globalization.CultureInfo.GetCultureInfo("ar-EG");
    Check(RelayAuth.MatchesProof(proof, challenge, 76561198377244747UL, roomId),
        "proof independent of system language");
}
finally
{
    System.Globalization.CultureInfo.CurrentCulture = originalCulture;
}
var alteredChallenge = (byte[])RelayAuth.ChallengePacket(challenge).Clone();
alteredChallenge[alteredChallenge.Length - 1] = (byte)'Z';
Check(!RelayAuth.TryReadChallenge(alteredChallenge, out _), "malformed challenge rejected");
Console.WriteLine($"PASS: snapshot, claim authority, claim protocol, and lobby-auth checks; snapshot {packet.Length} bytes.");
if (args.Length == 1)
{
    System.IO.File.WriteAllBytes(args[0], packet);
    Console.WriteLine("Snapshot packet written: " + args[0]);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
}
