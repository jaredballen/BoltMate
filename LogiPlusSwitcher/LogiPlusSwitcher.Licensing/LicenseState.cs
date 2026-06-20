namespace LogiPlusSwitcher.Licensing;

public enum LicenseState
{
    NotActivated = 0,
    Valid = 1,
    GracePeriod = 2,
    OfflineUnknown = 3,
    Expired = 4,
    Revoked = 5,
    SignatureInvalid = 6
}
