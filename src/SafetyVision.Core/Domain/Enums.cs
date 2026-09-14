namespace SafetyVision.Core.Domain;

public enum EquipmentCode
{
    Hardhat,
    Vest,
    Mask
}

public enum EquipmentStatus
{
    Worn,
    NotWorn,
    Unknown
}

public enum InspectionResultType
{
    Normal,
    CheckRequired,
    Unconfirmed
}

public enum DetectedClass
{
    Person,
    Hardhat,
    NoHardhat,
    Mask,
    NoMask,
    SafetyVest,
    NoSafetyVest
}

public enum InspectionState
{
    Waiting,
    PersonDetected,
    Inspecting,
    Result
}

public enum SaveState
{
    None,
    Saving,
    Saved,
    Failed
}

public enum CancelReason
{
    MultiplePersons,
    PersonLeft,
    DeviceError,
    InferenceError,
    DatabaseError,
    ScreenLeft,
    LoggedOut,
    ConnectionClosed
}

public enum PersonRoiCondition
{
    None,
    Multiple,
    TooSmall,
    TooClose,
    Qualified
}

public enum FrameVote
{
    NoInfo,
    Positive,
    Negative,
    Conflict
}
