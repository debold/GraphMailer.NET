# GraphMailer.NET – Test Documentation

**Total: 688 tests** (641 unit · 47 integration) plus **9 opt-in live tests** against a real M365 test tenant — last updated 2026-07-18

> **Maintenance rule**: Every new test must be documented in this file before the PR/commit is considered complete.  
> Add a row to the matching section. If a new section is needed, follow the existing heading pattern.

---

## Unit Tests — `GraphMailer.Tests.Unit`

### AuthHandler (`Infrastructure/Security/AuthHandlerTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `ValidateUser_PlaintextPassword_ValidCredentials_ReturnsTrue` | Correct username + correct plaintext password | `true` |
| `ValidateUser_PlaintextPassword_WrongPassword_ReturnsFalse` | Correct username + wrong plaintext password | `false` |
| `ValidateUser_UnknownUser_ReturnsFalse` | Username does not exist in config | `false` |
| `ValidateUser_UsernameIsCaseInsensitive` | Username stored as `Alice`, validated as `ALICE` | `true` (case-insensitive match) |
| `ValidateUser_NoUsers_ReturnsFalse` | Empty user list in config | `false` |
| `ValidateUser_EncryptedPassword_ValidCredentials_ReturnsTrue` | Password stored as `ENC[...]`, correct plaintext supplied | `true` |
| `ValidateUser_EncryptedPassword_WrongPassword_ReturnsFalse` | Password stored as `ENC[...]`, wrong plaintext supplied | `false` |
| `ValidateUser_CorruptEncryptedPassword_ReturnsFalse` | `ENC[invalid-ciphertext]` in config | `false` |
| `ValidateUser_CorruptEncryptedPassword_LogsError` | `ENC[invalid-ciphertext]` in config | `LogError` containing `"Failed to decrypt"` is emitted so the operator is alerted |
| `ValidateUser_CaptureMode_LogsWarningAboutOpenWindow` | `CaptureNextPassword = true`, arbitrary password | Auth succeeds, capture triggered, and a `Warning` containing "ANY password" is logged (the open capture window is security-relevant) |
| `ValidateUser_NullPassword_ReturnsFalse` | `Password = null` (unconfigured) | `false` |
| `ValidateUser_EmptyPassword_ReturnsFalse` | `Password = ""` | `false` |
| `ValidateUser_EmptyUsername_ReturnsFalse` | Empty string as username | `false` |
| `ValidateUser_MultipleUsers_CorrectUserSelected` | Three users; validates each independently | Correct user accepted, wrong user/password rejected |
| `GetFromRestrictions_UserWithRestrictions_ReturnsThemList` | User has `FromRestrictions` configured | Returns the restriction list |
| `GetFromRestrictions_UserWithoutRestrictions_ReturnsNull` | User exists but has no `FromRestrictions` | `null` |
| `GetFromRestrictions_UnknownUser_ReturnsNull` | Username not in config | `null` |
| `GetFromRestrictions_CaseInsensitiveUsername` | Username stored as `Dave`, queried as `DAVE` / `dave` | Restrictions returned (case-insensitive) |
| `ValidateUser_DisabledUser_ReturnsFalse_EvenWithCorrectPassword` | `Enabled = false`, correct password | `false` — disabled users cannot authenticate |
| `ValidateUser_DisabledUser_CaptureModeDoesNotBypass` | `Enabled = false` + `CaptureNextPassword = true` | `false`, capture not triggered |
| `ValidateUser_EnabledDefaultsToTrue_ForConfigsWithoutTheFlag` | UserEntry without explicit Enabled (older config) | `true` — default keeps old configs working |
| `ValidateUser_Failure_ReportsReason` (Theory) | Unknown user / wrong password | `failureReason` = `"unknown user"` / `"wrong password"` |
| `ValidateUser_DisabledUser_ReportsReason` | Disabled user | `failureReason` = `"user is disabled"` |
| `ValidateUser_NoPasswordConfigured_ReportsReason` | `Password = null` | `failureReason` = `"no usable password configured"` |
| `ValidateUser_Success_ReasonIsNull` | Valid credentials | `failureReason` = `null` |

---

### BuildInfo (`Infrastructure/BuildInfoTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `FileVersion_IsFourPartVersion_FromBuild` | Read assembly file version | Matches `^\d+\.\d+\.\d+\.\d+$` (real build version, not "unknown") |
| `InformationalVersion_IsNotEmpty` | Read informational version | Non-empty |

---

### SingleInstanceGuard (`Infrastructure/SingleInstanceGuardTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `FirstInstance_LockFree_IsPrimary` | No mutex held yet for the name | `IsPrimaryInstance` = `true` |
| `SecondInstance_LockHeld_IsNotPrimary` | A first guard already holds the named lock | second guard's `IsPrimaryInstance` = `false` |
| `NewInstance_AfterPrimaryDisposed_IsPrimary` | Primary disposed (mutex released) before a new guard is created | new guard's `IsPrimaryInstance` = `true` |
| `Dispose_AsNonPrimary_DoesNotThrow` | Dispose a guard that never owned the mutex | no exception (no `ReleaseMutex` on a non-owner) |

---

### IpBlockingService (`Infrastructure/Security/IpBlockingServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `IsBlocked_WhenDisabled_AlwaysReturnsFalse` | Blocking globally disabled | `IsBlocked` always `false`, failures ignored |
| `RecordFailure_BelowThreshold_IpNotBlocked` | 2 failures, threshold = 3 | `IsBlocked` = `false` |
| `RecordFailure_AtThreshold_IpBlocked` | 3 failures, threshold = 3 | `IsBlocked` = `true` |
| `RecordFailure_DifferentIps_AreIndependent` | IP A reaches threshold, IP B does not | A blocked, B not blocked |
| `GetBlockedIps_AfterBlock_ContainsIp` | IP blocked after reaching threshold | IP appears in `GetBlockedIps()` snapshot |
| `GetBlockedIps_UnblockedIp_NotIncluded` | IP below threshold | IP absent from `GetBlockedIps()` |
| `RecordFailure_MixedTypes_CountCombined` | 1× `authFailure` + 1× `blockedSender`, threshold = 2 | `IsBlocked` = `true` (failure types are combined) |
| `RecordFailure_FailuresOutsideTimeframe_NotCounted` | 2 failures at t=0, 1 failure at t=61 s, timeframe = 60 s, threshold = 3 | Only the 1 failure inside the window counts → not blocked |
| `RecordFailure_FailuresSpanningTwoWindows_OnlyRecentCounted` | 1 failure at t=0, 2 failures at t=59 s, timeframe = 60 s, threshold = 3 | All 3 are within the same 60 s window → blocked |
| `IsBlocked_AfterBlockExpires_ReturnsFalse` | Block duration = 10 s, clock advanced 11 s | `IsBlocked` = `false` |
| `IsBlocked_BeforeBlockExpires_ReturnsTrue` | Block duration = 30 s, clock advanced 29 s | `IsBlocked` = `true` |
| `GetBlockedIps_AfterBlockExpires_ExcludesExpiredEntry` | Block expires; `IsBlocked` called to trigger lazy removal | Expired entry absent from `GetBlockedIps()` |
| `Sweep_RemovesStaleFailureHistories_OfOneOffIps` | Two IPs fail once, tracking window elapses, `Sweep()` | `TrackedFailureIpCount == 0` (regression: one-off IPs kept an entry forever — IPv6-rotation memory growth) |
| `Sweep_KeepsRecentFailures_AndRemovesExpiredBlocks` | Block expired but failure still inside the window | Block swept; failure history kept |

---

### IpFilterService (`Infrastructure/Security/IpFilterServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `IsAllowed_EmptyLists_AllowsAnyIp` | No whitelist, no blacklist | Every IP allowed |
| `IsAllowed_BlacklistedIp_ReturnsFalse` | Exact IP in blacklist | `false` |
| `IsAllowed_BlacklistedCidr_ReturnsFalse` | IP falls inside blacklisted CIDR | `false` |
| `IsAllowed_IpOutsideBlacklistedCidr_ReturnsTrue` | IP outside blacklisted CIDR | `true` |
| `IsAllowed_WhitelistedIp_ReturnsTrue` | Exact IP in whitelist | `true` |
| `IsAllowed_NonWhitelistedIp_ReturnsFalse` | IP not in whitelist (whitelist is set) | `false` |
| `IsAllowed_WhitelistedCidr_ReturnsTrue` | IP falls inside whitelisted CIDR | `true` |
| `IsAllowed_IpInBothLists_BlacklistWins` | IP in both whitelist and blacklist | `false` (blacklist takes priority) |
| `IsAllowed_IPv6Loopback_WithWhitelist_Allowed` | `::1` with `::1/128` whitelist | `true` |
| `IsAllowed_IPv6_Cidr_Blacklist` | IPv6 address inside blacklisted CIDR | `false` |
| `IsAllowed_InvalidIp_ReturnsFalse` | Non-parseable IP string | `false` |
| `IsAllowed_MalformedCidrEntry_IsSkipped` | One malformed + one valid entry in whitelist | Malformed entry ignored; valid entry matches → `true` |
| `IsAllowed_BareIpInWhitelist_Matched` | Plain IP (no CIDR suffix) in whitelist | `true` |
| `GetDenyReason_BlacklistedByCidr_NamesTheEntry` | IP covered by blacklist CIDR | `"matches IP blacklist entry '203.0.113.0/24'"` |
| `GetDenyReason_NotInWhitelist_SaysSo` | Whitelist non-empty, IP not covered | `"not covered by any IP whitelist entry"` |
| `GetDenyReason_UnparsableIp_SaysSo` | `"unknown"` as remote IP | `"remote IP could not be parsed"` |
| `GetDenyReason_BlacklistWinsOverWhitelist` | IP in both lists | Blacklist match reported (mirrors `IsAllowed` precedence) |

---

### MailAddressFilter (`Infrastructure/Smtp/MailAddressFilterTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `IsAllowed_EmptyBothLists_AllowsAnyAddress` | No allow list, no block list | Every address allowed |
| `IsAllowed_AllowList_ExactMatch_Allowed` | Exact address in allow list | `true` |
| `IsAllowed_AllowList_NoMatch_Denied` | Address not in allow list (allow list is set) | `false` |
| `IsAllowed_AllowList_DomainWildcard_Match_Allowed` | `@example.com` matches `user@example.com` | `true` |
| `IsAllowed_AllowList_DomainWildcard_SubdomainNotMatched_Denied` | `@example.com` does NOT match `user@sub.example.com` | `false` |
| `IsAllowed_AllowList_DomainWildcard_OtherDomain_Denied` | `@example.com` does not match `user@other.com` | `false` |
| `IsAllowed_AllowList_MultipleEntries_FirstMatches_Allowed` | Address matches second entry in a three-entry list | `true` |
| `IsAllowed_AllowList_MultipleEntries_NoneMatch_Denied` | No entry in allow list matches | `false` |
| `IsAllowed_BlockList_ExactMatch_Denied` | Exact address in block list | `false` |
| `IsAllowed_BlockList_NoMatch_Allowed` | Address not in block list | `true` |
| `IsAllowed_BlockList_DomainWildcard_Match_Denied` | `@blocked.com` matches `anyone@blocked.com` | `false` |
| `IsAllowed_BlockList_DomainWildcard_OtherDomain_Allowed` | `@blocked.com` does not match `user@safe.com` | `true` |
| `IsAllowed_AddressInBothLists_BlockListWins_Denied` | Address in both allow and block list | `false` (block list wins) |
| `IsAllowed_DomainInBothLists_BlockListWins_Denied` | Domain wildcard in both lists | `false` (block list wins) |
| `IsAllowed_AllowedByDomain_BlockedByExactAddress_Denied` | Whole domain allowed, specific address blocked | `false` |
| `IsAllowed_BlockedByDomain_AllowedByExactAddress_Denied` | Specific address allowed, whole domain blocked | `false` (block list wins) |
| `IsAllowed_ExactMatch_CaseInsensitive_Allowed` | Mixed-case address matched against lower-case list entry | `true` |
| `IsAllowed_DomainWildcard_CaseInsensitive_Allowed` | Mixed-case address matched against lower-case domain wildcard | `true` |
| `IsAllowed_BlockList_CaseInsensitive_Denied` | Upper-case address matched against lower-case block entry | `false` |
| `IsAllowed_NullReversePath_EmptyLists_Allowed` | `MAIL FROM:<>` → address `"@"`, no filter lists | `true` (NDRs must pass through) |
| `IsAllowed_NullReversePath_AllowListSet_Denied` | `MAIL FROM:<>` → address `"@"`, allow list is set | `false` (no list entry matches `"@"`) |
| `MatchesAny_EmptyList_ReturnsFalse` | `MatchesAny` called with empty list | `false` |
| `MatchesAny_DomainWildcard_SubdomainSuffix_NotMatched` | `user@xexample.com` vs. `@example.com` | `false` (suffix-spoofing guard) |
| `GetDenyReason_BlockedByExactEntry_NamesTheEntry` | Address in block list | `"matches block list entry 'spam@example.com'"` |
| `GetDenyReason_BlockedByDomainWildcard_NamesTheWildcard` | Address matches `@bad.org` wildcard | `"matches block list entry '@bad.org'"` |
| `GetDenyReason_NotInAllowList_SaysSo` | Allow list non-empty, address not covered | `"not covered by any allow list entry"` |
| `GetDenyReason_BlockListWinsOverAllowList` | Address in both lists | Block match reported (mirrors `IsAllowed` precedence) |

---

### SmtpOptionsValidator (`Infrastructure/Validation/SmtpOptionsValidatorTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Validate_DefaultMaxSizeBytes_Succeeds` | Default `SmtpOptions` (25 MB) | Validation succeeds |
| `Validate_ValidMaxSizeBytes_Succeeds` _(Theory)_ | Values 1 byte, 1 KB, 25 MB, ~35 MB, 150 MB | Each succeeds |
| `Validate_ZeroOrNegativeMaxSizeBytes_Fails` _(Theory)_ | `MaxSizeBytes` = 0, -1, `long.MinValue` | Validation fails; error message contains `"MaxSizeBytes"` |
| `Validate_AboveExchangeOnlineLimit_SucceedsWithWarning` _(Theory)_ | Values > 150 MB (e.g. 200 MB) | Validation succeeds (warning logged, no startup failure) |
| `ExchangeOnlineMaxBytes_Is150Mb` | Constant value check | `SmtpOptionsValidator.ExchangeOnlineMaxBytes == 150 * 1024 * 1024` |

---

### GraphApiOptionsValidator (`Infrastructure/Validation/GraphApiOptionsValidatorTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Validate_NothingConfigured_Succeeds` | All fields null/empty | Validation succeeds (SMTP intake must still start) |
| `Validate_NothingConfigured_LogsWarning` | All fields null/empty | Warning containing `"Graph API credentials are not configured"` logged |
| `Validate_WithClientSecret_Succeeds` | TenantId + ClientId + ClientSecret | Validation succeeds |
| `Validate_WithCertificateThumbprint_Succeeds` | TenantId + ClientId + ClientCertificateThumbprint | Validation succeeds |
| `Validate_WithCertificateSubjectName_Succeeds` | TenantId + ClientId + ClientCertificateSubjectName | Validation succeeds |
| `Validate_BothSecretAndCert_SucceedsWithWarning` | All four credential fields set | Succeeds; Warning `"certificate will be used"` logged |
| `Validate_MissingTenantId_Fails` | ClientId + ClientSecret, TenantId null | Validation fails; error contains `"TenantId"` |
| `Validate_MissingClientId_Fails` | TenantId + ClientSecret, ClientId null | Validation fails; error contains `"ClientId"` |
| `Validate_MissingClientSecret_Fails` | TenantId + ClientId, no auth method set | Validation fails; error mentions `ClientSecret` or `ClientCertificateSubjectName` |

---

### DataProtection / Encryption (`Infrastructure/Encryption/EncryptionTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Protect_Unprotect_Roundtrip` | Protect a string, then unprotect with same protector | Decrypted value equals original |
| `Protect_ProducesEncFormat_ThatProviderRecognises` | `ENC[<ciphertext>]` format | String starts with `ENC[` and ends with `]` |
| `Unprotect_WithWrongKey_ThrowsCryptographicException` | Unprotect ciphertext using a different key ring | `CryptographicException` thrown |
| `Protect_SameInput_ProducesDifferentCiphertexts` | Same plaintext protected twice | Two different ciphertexts (random nonce) |

---

### SecretIntegrityChecker (`Infrastructure/Encryption/SecretIntegrityCheckerTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `NoEncryptedValues_ReturnsEmpty` | Config JSON without any `ENC[...]` values | Empty result (nothing to verify) |
| `AllValuesDecryptable_ReturnsEmpty` | `ENC[...]` value decryptable with the given protector | Empty result |
| `UndecryptableValue_ReturnsItsPath` | `ENC[...]` encrypted with a different key ring | Returns `["GraphApi.ClientSecret"]` |
| `UndecryptableValueInArray_ReturnsIndexedPath` | Broken secret nested in an array | Returns `["Users[0].Password"]` (indexed path) |
| `MixedValidAndInvalid_ReturnsOnlyInvalidPaths` | One decryptable + one undecryptable value | Only the undecryptable path returned |
| `GarbageCipher_IsReportedAsUndecryptable` | `ENC[not-real-ciphertext]` | Path reported as undecryptable |
| `NonStringValues_AreIgnored` | Numbers / booleans / null in JSON | Empty result (only strings inspected) |
| `Scan_CountsTotalEncryptedValues` | Two `ENC[...]` values (decryptable) + one plain string | `TotalEncrypted` = 2, no undecryptable |
| `Scan_ReportsTotalAndFailuresForMixedDocument` | Two values encrypted with a foreign key ring | `TotalEncrypted` = 2, both paths undecryptable |

---

### BackupCrypto (`Infrastructure/Backup/BackupCryptoTests.cs`)

Password-based container: PBKDF2-HMAC-SHA256 + AES-256-GCM (header authenticated as AAD).

| Test | Scenario | Expected result |
|---|---|---|
| `EncryptThenDecrypt_SamePassword_RoundTrips` | Encrypt then decrypt with the same password | Plaintext recovered byte-for-byte |
| `Decrypt_WrongPassword_ThrowsDecryptionException` | Decrypt with a different password | `BackupDecryptionException` |
| `Decrypt_TamperedCiphertext_ThrowsDecryptionException` | Flip a ciphertext byte | `BackupDecryptionException` (GCM auth fails) |
| `Decrypt_TamperedHeader_ThrowsDecryptionException` | Flip a salt byte in the authenticated header | `BackupDecryptionException` |
| `Decrypt_BadMagic_ThrowsFormatException` | Corrupt the magic bytes | `BackupFormatException` |
| `Decrypt_TruncatedFile_ThrowsFormatException` | 3-byte input | `BackupFormatException` |
| `Encrypt_EmptyPassword_Throws` | Empty password | `ArgumentException` |
| `Encrypt_SameInputTwice_ProducesDifferentContainers` | Encrypt same input twice | Different output (random salt + nonce) |

---

### BackupArchive (`Infrastructure/Backup/BackupArchiveTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `BuildThenRead_RoundTripsManifestAndConfig` | Pack manifest + config, then read | Manifest fields and config JSON preserved |
| `Read_NonArchiveBytes_ThrowsFormatException` | Non-ZIP bytes | `BackupFormatException` |
| `EncryptedRoundTrip_ThroughCrypto_PreservesConfig` | Archive → encrypt → decrypt → read | Config JSON preserved end-to-end |

---

### ConfigBackupService (`Infrastructure/Backup/ConfigBackupServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `BuildBackup_ThenReadManifest_RoundTrips` | Build a backup, read its manifest | Manifest has source machine + recent timestamp |
| `ReadManifest_WrongPassword_Throws` | Read manifest with wrong password | `BackupDecryptionException` |
| `Restore_RewritesConfig_WithSecretsReEncryptedUnderLocalKey` | Restore to a fresh path | Secrets written as `ENC[...]` and decrypt back to originals |
| `Rotate_KeepsNewest_DeletesOldest` | 5 backups, max 3 | 2 oldest deleted, 3 newest kept |
| `Rotate_FewerThanMax_DeletesNothing` | 1 backup, max 5 | Nothing deleted |

---

### BackupSchedule (`Infrastructure/Backup/BackupScheduleTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Daily_TimeLaterToday_ReturnsToday` | Daily, time later today | Today at the configured time |
| `Daily_TimeAlreadyPassed_ReturnsTomorrow` | Daily, time already passed | Tomorrow at the time |
| `Weekly_SameDayLaterTime_ReturnsToday` | Weekly on today, time later | Today |
| `Weekly_SameDayPassedTime_ReturnsNextWeek` | Weekly on today, time passed | +7 days |
| `Weekly_LaterInWeek_ReturnsThatDay` | Weekly on a later weekday | That day this week |
| `NextRun_InvalidTimeOfDay_ReturnsNull` | `TimeOfDay` not "HH:mm" | `null` |
| `NextRun_FromOptions_ParsesTimeAndFrequency` | From `BackupOptions` | Parsed correctly (03:30 passed → tomorrow) |

---

### BackupBackgroundService (`Services/BackupBackgroundServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `RunBackup_Success_NotifiesSucceeded` | Backup written, rotation runs | `NotifyBackupResultAsync(true, …)` sent; no failure notification |
| `RunBackup_WriteFails_NotifiesFailed_AndDoesNotRotate` | `WriteBackup` throws (disk full) | No rotation; `NotifyBackupResultAsync(false, …)` with the reason |
| `PlanTick_HoldsTarget_FiresAtScheduledTime_ThenRescheduges` | Before → at → after scheduled time | `Wait` → `Run` → `Wait` (regression: never fired before because the target was recomputed every tick) |
| `PlanTick_AfterOptionsChange_AdoptsNewTime_EvenIfLater` | Options change to a later time after a target was set | New target adopted (hot reload, no restart) |
| `PlanTick_Disabled_ReturnsIdle` | `Enabled = false` | `Idle` |
| `PlanTick_EnabledButNoPassword_ReturnsIdle` | No backup password | `Idle` |
| `PlanTick_InvalidTime_ReturnsIdle` | `TimeOfDay` not parseable | `Idle` |

---

### RetrySchedule (`Services/RetryScheduleTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `NextRetryInterval_WithinTransientPhase_UsesTransientInterval` | retryCount 1 and 6, transientCount 6 | Transient interval (300 s) |
| `NextRetryInterval_AfterTransientPhase_UsesSteadyInterval` | retryCount 7 and 50 | Steady interval (900 s) |
| `HasExpired_BeforeBudget_False` | received, now = +23 h, expiration 24 h | `false` |
| `HasExpired_AtOrAfterBudget_True` | now = +24 h / +30 h, expiration 24 h | `true` |
| `HasExpired_ZeroHours_AlwaysExpired` | expiration 0 h | `true` (give up on first failure) |
| `ApproxAttempts_DefaultPolicy_IsAroundHundred` | defaults (6×5min + 15min until 24 h) | 101 |
| `Describe_DefaultPolicy_IsHumanReadable` | default policy | Contains "every 5m", "first 6 retries", "then every 15m", "expires 24h" |
| `Describe_ExpirationZero_ExplainsImmediateFailure` | expiration 0 h | Text explains "immediately" |
| `Describe_NoTransientPhase_OmitsTransientWording` | transientCount 0 | "Retry every 15m"; no "first 0 retries" |

---

### ReportSchedule (`Services/Reporting/ReportScheduleTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `NextRun_InvalidTimeOfDay_ReturnsNull` | `TimeOfDay` not parseable | `null` |
| `NextRun_Weekly_BeforeSlotToday_ReturnsTodayWhenWeekdayMatches` | Weekly Monday 07:00, now Monday 06:00 | Today 07:00 |
| `NextRun_Weekly_AfterSlot_RollsToNextWeek` | Weekly Monday 07:00, now Monday 07:30 | Next Monday 07:00 |
| `NextRun_Weekly_DifferentWeekday_AdvancesToThatDay` | Weekly Friday, now Monday | Coming Friday 07:00 |
| `NextRun_Monthly_BeforeDayThisMonth_ReturnsThisMonth` | Monthly day 15, now the 10th | This month's 15th 07:00 |
| `NextRun_Monthly_AfterDay_RollsToNextMonth` | Monthly day 15, now the 20th | Next month's 15th 07:00 |
| `NextRun_Monthly_DayOfMonthClampedTo28` | Monthly day 31 in February | Clamped to the 28th (exists in every month) |

---

### ScheduledReportService (`Services/Reporting/ScheduledReportServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `PlanTick_ReportDisabled_ReturnsIdle` | `ScheduledReport.Enabled = false` | `Idle` |
| `PlanTick_NoSenderAddress_ReturnsIdle` | Report enabled but no sender address | `Idle` (paused, warning logged) |
| `PlanTick_NoRecipients_ReturnsIdle` | Report enabled but no admin recipients | `Idle` (paused, warning logged) |
| `PlanTick_BeforeSlot_Waits_AtSlot_Runs_ThenReschedules` | Before → at → after scheduled time | `Wait` → `Run` → `Wait` (regression: target held across ticks, not recomputed every tick) |
| `RunReportAsync_SendsHtmlToRecipients` | Enabled, sender + recipients set | `SendHtmlNotificationAsync` called once with the sender, the recipients, an "Operations Report" subject and an HTML document body |

---

### HtmlReportRenderer (`Services/Reporting/HtmlReportRendererTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Render_ProducesWellFormedHtmlDocument_WithCidChartImage` | Render a sample report | Full HTML doc with title, host, thousands-formatted KPIs; chart referenced via `cid:` (no `<svg>`); `ChartPng` PNG bytes produced for attachment |
| `Render_NoFailedQueue_OmitsActionRequiredSection` | `FailedQueueCount = 0` | No "Action Required" section |
| `Render_WithFailedQueue_ShowsActionRequiredSection` | One failed-queue item | "Action Required" section with the item's error |
| `Render_HtmlEncodesUserControlledText` | Subject/sender/top-sender contain `<script>` and other markup | Raw tags are HTML-encoded (`&lt;script&gt;…`); no live markup in the output |

---

### ReportDataCollector (`Services/Reporting/ReportDataCollectorTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Collect_AggregatesDeliveredFailedAndTopHosts` | Seeded metrics DB: 2 received from one IP, 1 sent, 1 failed | `Delivered == 1`, `Failed == 1`, top hosts contains that IP with count 2 |
| `Collect_ReadsFailedQueueFolder` | One `*.meta.json` in `mail\failed\` | `FailedQueueCount == 1`; the item's subject and last error are surfaced |
| `Collect_SentWithDuration_ReportsAvgAndPeakDelivery` | Two sent events with 200 ms / 400 ms durations | `AvgDeliveryMs == 300` **and** `PeakDeliveryMs == 400` — regression: SQLite returns `MAX(duration_ms)` on the INT column as `long`, which was once dropped and rendered as "no data" |
| `Collect_NoMetricsDb_ReturnsZeroedStatsWithoutThrowing` | No metrics DB present | Stats are zero, health checks still populated, no exception (monthly title) |

---

### DailyChartImage (`Services/Reporting/DailyChartImageTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Render_ReturnsValidPngBytes` | Render 4 days of sent/failed data | Non-empty byte array beginning with the PNG magic number `89 50 4E 47 …` |
| `Render_SingleDay_DoesNotThrow` | One data point | Valid PNG (single point centred, no divide-by-zero) |
| `Render_AllZero_DoesNotThrow` | All sent/failed counts zero | Valid PNG (scale falls back to 1, no exception) |

---

### CertificateStoreService (`Infrastructure/Certificates/CertificateStoreServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `IsConfigured_NoSelector_ReturnsFalse` | `CertificateOptions` with no `SubjectName` / `FriendlyName` | `IsConfigured()` = `false` |
| `IsConfigured_WithSubjectName_ReturnsTrue` | `SubjectName` set | `IsConfigured()` = `true` |
| `LoadCertificate_NoSelector_ReturnsNull` | No selector configured | `null` |
| `LoadCertificate_InvalidStoreLocation_ReturnsNull` | `StoreLocation = "InvalidLocation"` | `null` (parsing fails gracefully) |
| `LoadCertificate_InvalidStoreName_ReturnsNull` | `StoreName = "InvalidStoreName"` | `null` (parsing fails gracefully) |
| `LoadCertificate_SubjectMatch_ReturnsCert` | Self-signed cert installed in `CurrentUser\My`; matching `SubjectName` | Certificate returned |
| `LoadCertificate_SubjectAndIssuerMatch_ReturnsCert` | Self-signed cert; `Issuer` filter matches self-signed issuer | Certificate returned |
| `LoadCertificate_IssuerMismatch_ReturnsNull` | Self-signed cert; `Issuer` filter set to a different CA | `null` |
| `LoadCertificate_MultipleMatches_ReturnsLatestExpiry` | Two certs with same subject but different validity periods | Cert with later `NotAfter` returned (renewal-safe) |
| `LoadCertificate_SubjectNotFound_ReturnsNull` | Subject not present in certificate store | `null` |

---

### MailQueueWriter (`Services/MailQueueWriterTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `WriteAsync_CreatesEmlAndMetaFiles` | Write one message | Exactly one `.eml` and one `.meta.json` in `mail/queue/` |
| `WriteAsync_EmlContainsCorrectBytes` | Write known byte array | `.eml` file byte content matches input exactly |
| `WriteAsync_MetaContainsCorrectFields` | Write with known sender, recipients, IP | `.meta.json` contains sender, all recipients, source IP, `"Status": "queued"` |
| `WriteAsync_MultipleMessages_EachGetUniqueId` | Write two messages | Two `.eml` and two `.meta.json` files with distinct names |
| `WriteAsync_NoTempFilesLeftOnSuccess` | Successful write | No `.tmp` files remain in `mail/queue/` |
| `WriteAsync_ReturnsMailMetadataWithCorrectFields` | Write with known sender/recipient/IP | Returned `MailMetadata` has matching `From`, `To`, `ClientIp`, `Status`, non-empty `MessageId` |
| `WriteAsync_ExtractsSubjectFromEmlHeaders` | EML has `Subject:` header | `meta.Subject` matches the header value |
| `WriteAsync_ExtractsSmtpMessageIdFromEmlHeaders` | EML has `Message-ID:` header | `meta.SmtpMessageId` contains the message ID without angle brackets |
| `WriteAsync_HandlesHeaderFolding` | Subject header has RFC 2822 folded continuation line | `meta.Subject` is the unfolded single-line value |
| `WriteAsync_DecodesEncodedWordSubject` | `Subject: =?utf-8?B?…?=` (RFC 2047) | `meta.Subject` is the decoded UTF-8 text (regression: was stored raw/unreadable) |
| `WriteAsync_SubjectAfterLargeHeaderBlock_IsStillExtracted` | ~12 KB of Received headers before Subject | Subject extracted (regression: the old 8 KB scan cutoff lost it) |
| `WriteAsync_SubjectLookalikeInBody_IsNotExtracted` | No Subject header; body contains `Subject: …` | `meta.Subject` empty — parsing stops at the blank line |

---

### SmtpMessageStore — DATA response contract (`Infrastructure/Smtp/SmtpMessageStoreTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `SaveAsync_QueueWriteSucceeds_Returns250AndQueuesPair` | Message written to `mail/queue/` successfully | SMTP 250; `.eml` + `.meta.json` pair in queue |
| `SaveAsync_MetricsThrowAfterQueueWrite_StillReturns250` | Metrics write throws after the message is durably queued | SMTP 250 (regression: a telemetry failure must not make the client re-send an already-queued message → duplicate delivery) |
| `SaveAsync_QueueWriteFails_ReturnsTransient451` | Queue directory replaced by a file → write throws (simulated disk/IO failure) | SMTP 451 transient (regression: the old permanent 554 made conforming clients discard the mail → silent mail loss) |
| `SaveAsync_BlockedIp_ReturnsPermanent550AndQueuesNothing` | Client IP blocked by `IpBlockingService` | SMTP 550 permanent, nothing queued (audit verification: `SmtpResponse.MailboxUnavailable` is 550, not a transient 4xx) |
| `SaveAsync_QueueWriteFails_LogsErrorWithException` | Queue write throws (same sabotage as the 451 test) | Single Error entry with the exception object attached — the log is the operator's only signal, and the attached exception is what puts the stack trace into the log file |

---

### QueueProcessor (`Services/QueueProcessorTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `ProcessBatch_GraphApiNotConfigured_DoesNotProcessFiles` | Graph API credentials not set, message in queue | Both files remain in `mail/queue/`, `IGraphApiClient.SendAsync` not called |
| `ProcessBatch_EmptyQueue_DoesNotCallGraphClient` | `mail/queue/` is empty | `IGraphApiClient.SendAsync` not called |
| `ProcessMessage_DeliverySucceeds_DeletesBothFiles` | Delivery succeeds, `ArchiveSentEmails = false` | Both `.eml` and `.meta.json` deleted from `mail/queue/` |
| `ProcessMessage_DeliverySucceeds_ArchivesFilesWhenEnabled` | Delivery succeeds, `ArchiveSentEmails = true` | Files moved to `mail/sent/`, `mail/queue/` empty |
| `ProcessMessage_FailedAttempt_PersistsLastAttemptAndError` | Delivery throws, retries remaining | meta.json updated with `LastError` text and `LastAttemptAt` timestamp |
| `ProcessMessage_PermanentFailure_FailedMetaContainsLastAttemptAndError` | Delivery throws, `MessageExpirationHours = 0` (give up on first failure) | Failed meta has `Status="failed"`, `LastError`, `LastAttemptAt` |
| `ProcessMessage_EmlMissing_FailedMetaContainsReason` | meta.json without matching .eml | Failed meta has `LastError = "EML file missing from queue directory"` |
| `ProcessMessage_DeliveredAfterRetry_SentMetaHasSentAtAndNoNextRetry` | Delivery succeeds on retry, archiving on | Sent meta has `SentAt`, `NextRetryAt = null`, `RetryCount` history kept |
| `ProcessMessage_FirstDeliveryFails_IncrementsRetryCountAndSetsBackoff` | First delivery attempt throws, `RetryCount = 0` | `RetryCount` = 1 in updated meta; `NextRetryAt` set in future |
| `ProcessMessage_TransientPhase_UsesTransientInterval` | First failure, defaults | `NextRetryAt` ≈ now + 300 s (transient interval) |
| `ProcessMessage_AfterTransientPhase_UsesSteadyInterval` | `RetryCount = 7` (beyond 6 transient), defaults | `NextRetryAt` ≈ now + 900 s (steady interval) |
| `ProcessMessage_ExpirationReached_MovesToFailed` | Delivery throws, received 25 h ago, expiration 24 h | Files moved to `mail/failed/`, `mail/queue/` empty (time-based give-up) |
| `ProcessMessage_BackoffNotElapsed_SkipsMessage` | `NextRetryAt` is one hour in the future | `IGraphApiClient.SendAsync` not called |
| `ProcessBatch_BackoffMessagesDoNotConsumeBatchBudget_EligibleMessageIsStillDelivered` | 10 back-off messages (sort first by GUID name) + 1 ready message, `BatchSize = 10` | Ready message is delivered (regression: head-of-line blocking — back-off entries no longer consume the batch budget); back-off entries not attempted |
| `ProcessMessage_MissingEmlFile_MovesToFailed` | Only `.meta.json` exists, no `.eml` | Meta moved to `mail/failed/`, `mail/queue/` empty |
| `CleanupOrphanedEmls_EmlWithoutMeta_MovesToFailed` | `.eml` without `.meta.json`, older than the 5-min grace period | EML + synthetic meta moved to `mail/failed/`; Warning logged |
| `CleanupOrphanedEmls_FreshOrphan_IsLeftForTheNextPass` | Orphan `.eml` younger than the grace period | Left in queue (the meta rename may still be in flight — cleanup now also runs hourly) |
| `CleanupOrphanedEmls_EmlWithMatchingMeta_IsNotTouched` | Complete `.eml` + `.meta.json` pair | Both files remain in `mail/queue/`, untouched |
| `ProcessBatch_CorruptMetaFile_MovesToFailed` | `.meta.json` contains invalid JSON | Both files moved to `mail/failed/`; Error logged |
| `CleanupSentEmails_ArchiveDisabled_DoesNothing` | `ArchiveSentEmails = false` | Files in `mail/sent/` untouched |
| `CleanupSentEmails_FileWithinRetention_IsKept` | `mtime` within retention window | File remains in `mail/sent/` |
| `CleanupSentEmails_ExpiredFile_DeletesBothFiles` | `mtime` older than `SentEmailRetentionDays` | `.eml` + `.meta.json` deleted from `mail/sent/` |
| `ProcessMessage_MetaAlreadyMarkedSent_CompletesCleanupWithoutResending` | Meta in queue with `SentAt` set (previous run crashed after Graph accepted the message) | `SendAsync` NOT called; queue emptied (regression: idempotency guard against duplicate delivery) |
| `ProcessMessage_MetaMarkedSentAndEmlAlreadyGone_ResumedArchiveCommitCompletes` | Archive mode; `SentAt` set, `.eml` already moved by the interrupted commit | `SendAsync` NOT called; meta lands in `mail/sent/`, queue emptied (re-entrant commit) |
| `ProcessMessage_ShutdownStartsDuringSend_CommitStillCompletes` | Shutdown token cancelled while `SendAsync` is in flight | Queue emptied anyway (regression: post-send commit runs on `CancellationToken.None`, not the shutdown token) |
| `ProcessMessage_MetricsFailAfterSend_MessageIsStillCompletedAndNotRetried` | `RecordEmailSentAsync` throws after a successful send | Exactly one `SendAsync`; queue emptied; no retry scheduled (metrics are telemetry only) |
| `ProcessMessage_PermanentGraphRejection_FailsImmediatelyAndSendsNdr` | `GraphDeliveryException(IsPermanent: true)`, expiration window NOT reached | Message moved to `failed/` on the first attempt; NDR sent (no 24 h retry churn) |
| `ProcessMessage_TransientGraphRejection_IsRetriedNotFailed` | `GraphDeliveryException(IsPermanent: false)` | Message stays in queue; `RetryCount = 1`, `NextRetryAt` set |
| `ProcessMessage_NotificationMetaFailsPermanently_NoNdrForTheNdr` | Meta with `IsNotification = true` fails permanently | Moved to `failed/`; `SendNdrAsync` NOT called (loop guard); admin `NotifyEmailDeliveryFailedAsync` still fires |
| `ProcessBatch_CorruptMetaFile_NotifiesAdmin` | Corrupt meta quarantined | `NotifyEmailDeliveryFailedAsync` called (regression: quarantine used to be log-only — silent mail loss) |
| `ProcessMessage_EmlMissing_SendsNdrAndNotifiesAdmin` | Meta readable, `.eml` missing | Admin notification AND NDR to the known sender |
| `ProcessBatch_DeliversMessagesInArrivalOrder_NotFilenameOrder` | "zzz" queued before "aaa" (creation times set) | Delivered in arrival order (FIFO), not GUID-filename order |
| `CleanupFailedEmails_ExpiredFile_DeletesBothFiles` | `mtime` older than `FailedEmailRetentionDays` | `.eml` + `.meta.json` deleted from `mail/failed/` |
| `CleanupFailedEmails_FileWithinRetention_IsKept` | `mtime` within retention window | File remains in `mail/failed/` |
| `CleanupFailedEmails_RetentionZero_KeepsEverything` | `FailedEmailRetentionDays = 0`, year-old file | File kept (0 = keep forever) |

---

### GraphApiClient — static delivery logic (`Services/GraphApiClientTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `IsPermanentRejection_PermanentCases_ReturnsTrue` (Theory, 8 cases) | HTTP 400/404/413 and error codes `ErrorInvalidRecipients`, `ErrorRecipientLimitExceeded`, `ErrorMessageSizeExceeded`, `MailboxNotEnabledForRESTAPI`, `ErrorInvalidUser` | `true` — fail fast, no retry can succeed |
| `IsPermanentRejection_TransientCases_ReturnsFalse` (Theory, 6 cases) | 429/5xx/408 and auth/permission problems (401, 403 `Authorization_RequestDenied`) | `false` — stays on the retry schedule (operator-fixable or outage) |
| `Rebalance_TotalUnderCap_MovesNothing` | Body + small attachments fit the 4 MB budget | Nothing moved to the upload-session path |
| `Rebalance_IndividuallySmallButCollectivelyLarge_MovesLargestUntilFit` | Three attachments < 3 MB each, > 4 MB total after base64 | Largest attachment(s) moved until the direct payload fits (regression: was rejected with 413) |
| `Rebalance_HugeBodyAloneOverCap_MovesAllAttachmentsAndStops` | 5 MB body, one small attachment | Loop terminates; all attachments moved (body alone cannot be fixed) |
| `Rebalance_MovedInlineAttachment_KeepsContentIdAndInlineFlag` | Inline attachment moved to the upload-session path | `ContentId` and `IsInline` survive the move (cid: reference keeps working) |
| `SendAsync_TooManyRecipients_ThrowsPermanentWithoutGraphCall` | 501 envelope recipients | `GraphDeliveryException` with `IsPermanent == true`, thrown before any Graph call |
| `BuildMessage_HighImportance_IsMapped` | MIME `Importance: high` | Graph `Importance.High` |
| `BuildMessage_XPriorityFallback_IsMapped` | `X-Priority: 1`, no Importance header | Graph `Importance.High` (legacy priority signal) |
| `BuildMessage_MessageIdAndThreadingHeaders_AreForwarded` | Message-ID, In-Reply-To, References set | `internetMessageId` + MAPI extended properties `0x1042`/`0x1039` populated |
| `BuildMessage_CustomXHeaders_AreForwarded_ReservedOnesAreNot` | `X-Legacy-App`, `X-MS-Exchange-*`, `X-Priority`, non-x header | Only `X-Legacy-App` forwarded (Graph allows x-* only; reserved/mapped ones skipped) |
| `CollectAttachments_InlineCidImage_KeepsContentIdAndInlineFlag` | multipart/related with inline PNG (`cid:`) | Small attachment carries `ContentId` + `IsInline == true` (regression: inline images showed as visible attachments) |

---

### GraphClientProvider — client caching (`Services/GraphClientProviderTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `GetClient_SameOptions_ReturnsCachedInstance` | Two calls with identical options | Same `GraphServiceClient` instance (cache hit) |
| `GetClient_RotatedClientSecret_RebuildsClient` | Second call with a different `ClientSecret` | New instance (regression: rotation used to keep the stale credential until restart) |

---

### Worker (`Services/WorkerTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Worker_StopsGracefully_WhenCancelled` | Start then stop | No exception thrown |
| `Worker_DoesNotThrow_WhenStoppedImmediately` | Start then stop immediately | No exception thrown |
| `Worker_StopAsync_CompletesWithinTimeout` | Stop with 5 s cancellation token | Completes well within timeout |

---

### SecretIntegrityCheckService (`Services/SecretIntegrityCheckServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `AllSecretsDecryptable_NoNotification_LogsInformation` | All `ENC[...]` values decrypt with the key ring | No admin notification; `Information` log "all secrets decryptable" |
| `UndecryptableSecret_LogsError_AndNotifiesWithFieldPath` | One secret encrypted with a foreign key ring | `LogError` "cannot be decrypted" + `NotifyConfigDecryptionFailedAsync` called with `["GraphApi.ClientSecret"]` |
| `NoConfigFile_NoNotification` | Config file does not exist | No notification (nothing to verify) |
| `InvalidJson_NoNotification_LogsWarning` | Config file is not valid JSON | No notification; `Warning` log "not valid JSON" |

---

### GraphApiMonitoringService (`Services/GraphApiMonitoringServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Check_ProbeFails_NotifiesOutageOnce` | Probe throws on two consecutive checks | Exactly 1 outage notification |
| `Check_ProbeRecovers_NotifiesRestored` | Probe fails, then succeeds | Restored notification fires |
| `Check_ProbeHealthy_NoNotifications` | Probe succeeds | No notifications |
| `Check_GraphNotConfigured_DoesNotProbe` | Graph credentials missing | Probe never called |
| `Check_MissingMailReadWrite_AlertsOncePerGap` | Token lacks Mail.ReadWrite, two checks | Exactly 1 alert naming the role |
| `Check_UserReadAll_RequiredOnlyWhenSenderValidationEnabled` | Token lacks User.Read.All | Alert only when sender validation is enabled |
| `Check_PermissionGapFixedAndReopened_AlertsAgain` | Gap → fixed → same gap again | Second alert fires after the reset |

---

### GraphConnectivityProbe (`Services/GraphConnectivityProbeTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `ParseRoles_TokenWithRoles_ReturnsThem` | JWT with `roles` claim | Role values extracted |
| `ParseRoles_TokenWithoutRolesClaim_ReturnsEmpty` | JWT without `roles` (no grants) | Empty list |
| `ParseRoles_GarbageInput_ReturnsEmpty` | Non-JWT strings | Empty list, no exception |

---

### PortProbeRegistry (`Services/PortProbeRegistryTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `IsProbeConnection_MarkedPortFromLoopback_ReturnsTrue` | Probe marked, loopback IP (v4/v6) | `true` — session logs demote to Debug |
| `IsProbeConnection_UnmarkedPort_ReturnsFalse` | Different port | `false` |
| `IsProbeConnection_NonLoopbackIp_ReturnsFalse` | Real client IP during probe window | `false` — real clients keep their log entry |
| `IsProbeConnection_ProbeWindowElapsed_ReturnsFalse` | Clock advanced past the window | `false` |

---

### TenantSenderDirectory (`Services/TenantSenderDirectoryTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `ValidateAsync_NullReversePath_AlwaysValid_NoGatewayCall` | `MAIL FROM:<>` normalized to `"@"` | `Valid`, no Graph lookup |
| `ValidateAsync_AfterRefresh_UpnAndAliasAreValid` | Full sync cached UPN + alias | Both `Valid` (case-insensitive), no on-demand lookup |
| `ValidateAsync_SharedMailbox_AccountDisabled_IsValid` | Shared mailbox (`AccountEnabled=false`) | `Valid` — sign-in state is irrelevant for sending |
| `RefreshAsync_ReplacesCache_RemovedUserNoLongerResolves` | Second sync returns empty tenant | Cache swapped atomically, address no longer resolves |
| `ValidateAsync_CacheMiss_OnDemandHit_PopulatesCache` | Alias unknown to cache, Graph lookup finds user | `Valid`; all user addresses cached; exactly 1 gateway call |
| `ValidateAsync_UnknownSender_NegativeCached` | Graph confirms address does not exist | `Unknown` twice, second answer from negative cache (1 gateway call) |
| `ValidateAsync_GraphNotConfigured_Indeterminate_NoGatewayCall` | Graph credentials missing | `Indeterminate` without Graph call |
| `ValidateAsync_GatewayThrows_Indeterminate_SingleAdminNotification` | Graph throws on two consecutive lookups | `Indeterminate` both times, exactly 1 admin notification per outage |
| `ValidateAsync_GraphRecoversAfterOutage_NotifiesAgainOnNextOutage` | Outage → recovery → second outage | 2 notifications total (flag reset on success) |
| `TryResolveGraphUserKey_Alias_ReturnsObjectId` | Alias of cached user | Returns the Graph object id |
| `TryResolveGraphUserKey_FeatureDisabled_ReturnsFalse` | Feature toggle off | `false` — send path unchanged |
| `RefreshAsync_Success_ReportsCounts` | Sync with 2 users / 3 addresses | Result has `Success=true`, counts for the status display |
| `RefreshAsync_GatewayThrows_ReportsFailureWithError` | Gateway throws | `Success=false`, error message in result |

---

### SenderDirectoryStatus (`Services/SenderDirectoryStatusTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `SaveAndTryLoad_RoundTripsAllFields` | Save → TryLoad | All fields preserved |
| `TryLoad_MissingFile_ReturnsNull` | File absent | `null`, no exception |
| `TryLoad_CorruptFile_ReturnsNull` | Invalid JSON | `null`, no exception |

---

### ConfigService — secret decryption resilience (`Infrastructure/Config/ConfigServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Load_CorruptEncClientSecret_ReportsFailure_AndDoesNotThrow` | `GraphApi.ClientSecret` is an undecryptable `ENC[...]` | No throw; `DecryptionFailures = ["GraphApi.ClientSecret"]`; secret blank, `TenantId` still loaded |
| `Load_CorruptEncUserPassword_ReportsFailure_WithIndex` | One user with an undecryptable password | `DecryptionFailures` contains `Users[0]`; user still loaded, only password blank |
| `Load_CorruptEncSecondUser_LoadsFirstUser_AndReportsSecondIndex` | First user's password valid, second user's broken | First password decrypted; second blank; `DecryptionFailures` reports `Users[1]` |
| `Load_AllSecretsValid_HasNoDecryptionFailures` | All `ENC[...]` values decrypt | `DecryptionFailures` empty; secret decrypted |
| `Backup_SaveThenLoad_RoundTripsValues_AndDecryptsPassword` | Save then load the `Backup` section | All fields round-trip; password written `ENC[...]` and decrypted back |
| `Load_OmittedScalar_TakesDefaultFromAppSettings_NotHardCodedLiteral` | Config omits a field; bundled `appsettings.json` carries a non-literal default | Loaded value comes from the appsettings overlay (single default source), not the code fallback |
| `Load_UserValue_OverridesAppSettingsDefault` | Config sets a field that also has an appsettings default | User value wins |
| `Load_DefaultsOverlay_DoesNotMaterialiseIntoRawSource` | Load a config that omits a section present in appsettings | Overlay feeds `Read*` but `RawSource` stays the untouched user document (Save round-trips cleanly) |
| `MergeDefaults_ObjectsRecurse_ScalarsAndArraysReplaced` | Merge a defaults object with a user object | Objects merge recursively; scalars/arrays replaced by the user value (empty user array overrides — no index-merge) |

---

### Configuration loading — missing config directory (`Infrastructure/Encryption/ConfigurationBuilderExtensionsTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `AddEncryptedJsonFile_MissingDirectory_CreatesItAndDoesNotThrow` | Build config with `reloadOnChange` on a config dir that does not exist yet (fresh install) | No throw; the watched config directory is created (regression for the fresh-install service-start crash) |

---

### DefaultConfiguration — shared first-run array defaults (`Infrastructure/Config/DefaultConfigurationTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Servers_AreIndustryStandard_25_465_587_AllEnabled` | Default listeners | Ports 25, 465, 587, all `Enabled` |
| `Servers_ModesAndAuth_MatchProtocolConventions` | Default listener modes/auth | 25 Plain/None, 465 Tls/Optional, 587 StartTls/Optional |
| `IpWhitelist_CoversPrivateRanges` | Default whitelist | RFC1918 + loopback + IPv6 ULA/link-local CIDRs |
| `IpWhitelistComments_HaveAnEntryForEveryWhitelistRange` | Whitelist comments | Every default whitelist entry has a non-empty comment |

---

### SelfSignedSmtpCertificate — in-memory creation (`Infrastructure/Certificates/SelfSignedSmtpCertificateTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `CreateSelfSigned_HasExpectedSubjectAndPrivateKey` | Build the self-signed cert | CN `GraphMailer SMTP`, has a private key, RSA-2048 |
| `CreateSelfSigned_HasServerAuthenticationEku` | Build the self-signed cert | EKU contains Server Authentication (`1.3.6.1.5.5.7.3.1`) |
| `CreateSelfSigned_HasSanWithLocalhostAndMachineName` | Build the self-signed cert | SAN contains `localhost` and the machine name |

---

### FirstRunProvisioner — seed config on fresh install (`Infrastructure/Config/FirstRunProvisionerTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `EnsureProvisioned_NoFile_SeedsListenersWhitelistAndCert` | No `graphmailer.json`; cert step returns a subject | File seeded with 25/465/587 listeners, private-range whitelist (+comments), and bound `Certificate.SubjectName` |
| `EnsureProvisioned_CertUnavailable_StillSeedsButLeavesSubjectUnset` | No file; cert step returns null (e.g. non-elevated) | Listeners/whitelist seeded; `SubjectName` left null (runtime plain fallback applies) |
| `EnsureProvisioned_ExistingFile_IsNoOp` | `graphmailer.json` already exists | No-op; existing content untouched, no listeners seeded |

---

### SmtpRelayService — listener selection & validation (`Services/SmtpRelayServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `SelectActiveListeners_ExcludesDisabledEntries` | Mixed enabled/disabled listeners | Only enabled ports returned (disabled never started) |
| `SelectActiveListeners_AllEnabled_ReturnsAll` | All listeners enabled | All returned |
| `SelectActiveListeners_AllDisabled_ReturnsEmpty` | All listeners disabled | Empty (relay inactive) |
| `SelectStartableListeners_InvalidPort_IsSkippedAndLoggedAsError` | Ports `0` and `70000` between a valid `25` | Only port 25 returned; `LogError` per skipped listener (regression: an invalid port used to crash the whole service) |
| `SelectStartableListeners_DuplicatePort_FirstEntryWinsSecondIsSkipped` | Two listeners on port 587 | First kept, second skipped with `LogError` |
| `SelectStartableListeners_AllValid_ReturnsAllWithoutLogging` | Three valid, distinct ports | All returned, nothing logged |
| `EffectiveMaxMessageSize_AboveIntMax_ClampsAndWarns` | `MaxSizeBytes` > `int.MaxValue` (~2 GB) | Clamped to `int.MaxValue` with a Warning (advertised SIZE differs from config) |
| `EffectiveMaxMessageSize_WithinRange_PassesThroughSilently` | Default 25 MB value | Passed through unchanged, no log |

---

### ConfigService — SenderValidation (`Infrastructure/Config/ConfigServiceSenderValidationTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Load_NoFile_SenderValidationDefaults` | No config file | `Enabled=false`, `RefreshIntervalMinutes=60`, `FailClosed=false` |
| `RoundTrip_SenderValidation_PreservesAllValues` | Save → Load with non-default values | All values preserved |
| `Save_WritesSectionNameMatchingServiceOptions` | Save with feature enabled | JSON contains `"SenderValidation"` section with `RefreshIntervalMinutes` |

---

### Backup page validation rules — ConfigTool (`ConfigTool/BackupPasswordRuleTests.cs`)

Validation rules of the Backup page (`BackupPage.ValidatePasswordRule` /
`ValidateEmailBackupRule`): an enabled schedule without a password would silently never run
(the service pauses it with only a log warning), and enabled email backups without a
recipient are silently skipped by the service. Both block Save.

| Test | Scenario | Expected result |
|---|---|---|
| `EnabledSchedule_NoPassword_IsAnError` | Schedule on, both password fields empty | Error "required for scheduled backups" (regression: was saveable without any hint) |
| `DisabledSchedule_NoPassword_IsValid` | Schedule off, no password | No error (manual backups enforce the password themselves) |
| `EnabledSchedule_ValidPassword_IsValid` | Schedule on, valid matching password | No error |
| `TooShortPassword_IsAnError_RegardlessOfSchedule` (Theory) | Password below 8 characters | "at least" error, schedule state irrelevant |
| `MismatchedConfirmation_IsAnError_RegardlessOfSchedule` (Theory) | Password ≠ confirmation | "do not match" error, schedule state irrelevant |
| `EmailBackupsEnabled_NoRecipients_IsAnError` | Email backups on, empty recipient list | Error "at least one recipient" (regression: the service silently skips the email step in this state) |
| `EmailBackupsEnabled_NoSender_IsAnError_PointingToTheNotificationsPage` | Email backups on, recipient present, no notification sender | Error naming the Notifications page (the cross-page dependency was otherwise invisible) |
| `EmailBackupsEnabled_RecipientAndSenderPresent_IsValid` | Email backups on, recipient + sender configured | No error |
| `EmailBackupsDisabled_NothingElseMatters_IsValid` | Email backups off | No error regardless of recipients/sender |

---

### Sender address rule — ConfigTool (`ConfigTool/SenderAddressRuleTests.cs`)

Cross-page rule (`SenderAddressRule`): Graph app-only auth has no fallback account, so a
missing sender silently disables admin notifications, NDRs, scheduled reports and emailed
backups. The rule feeds the inline error on the Notifications page and the save-time gate.

| Test | Scenario | Expected result |
|---|---|---|
| `NoSender_WithRecipients_IsAnError` | Sender empty, admin recipients present | Error naming "admin notifications" |
| `NoSender_WithNdrEnabled_IsAnError` | Sender empty, NDR on | Error naming "non-delivery reports" |
| `NoSender_WithReportEnabled_IsAnError` | Sender null, scheduled report on | Error naming "scheduled reports" |
| `NoSender_WithEmailedBackups_IsAnError` | Sender whitespace, backup email on | Error naming "emailed backups" |
| `NoSender_AllDependentsListed` | Sender empty, all four features active | Error lists every dependent feature |
| `NoSender_NothingDependsOnIt_IsValid` | Fresh install: nothing depends on the sender | No error |
| `InvalidSenderFormat_IsAnError_EvenWithoutDependents` | `not-an-email`, no dependents | Format error (a typo'd address must not be persisted) |
| `ValidSender_WithAllDependents_IsValid` | Valid address, all features active | No error |
| `DocumentOverload_CoversTheCrossPageBackupEmailDependency` | ConfigDocument with Backup.EmailEnabled only | Error — the overload sees the Backup page's toggle |

---

### DataProtection — config protector lifetime (`Infrastructure/Encryption/DataProtectionExtensionsTests.cs`)

Regression for the ConfigTool save failure: `BuildConfigProtector` must keep its backing
ServiceProvider alive, or `Protect()` throws "An error occurred while trying to encrypt the
provided data" (disposed provider). Windows-only.

| Test | Scenario | Expected result |
|---|---|---|
| `BuildConfigProtector_CanProtectAfterReturn_NotOnlyUnprotect` | Build the protector, then `Protect` → `Unprotect` | Round-trips (regression: `Protect` used to throw after the provider was disposed) |
| `BuildConfigProtector_TwoInstances_ShareTheSameKeyRing` | Two independently built protectors (service + ConfigTool) | A value encrypted by one decrypts with the other |

---

### DecryptionFailureMap — ConfigTool (`ConfigTool/DecryptionFailureMapTests.cs`)

Maps `ConfigDocument.DecryptionFailures` paths to the UI elements that flag undecryptable secrets inline.

| Test | Scenario | Expected result |
|---|---|---|
| `UserPasswordIndex_UserPath_ReturnsIndex` (Theory) | `Users[0].Password`, `Users[12].Password` | Returns 0 / 12 |
| `UserPasswordIndex_NonUserPasswordPath_ReturnsNull` (Theory) | `GraphApi.ClientSecret`, `Users[].Password`, `Users[0].Username`, `""` | `null` |
| `HasGraphApiFailure_WhenClientSecretPresent_ReturnsTrue` | Paths include `GraphApi.ClientSecret` | `true` |
| `HasGraphApiFailure_WhenOnlyUserPasswords_ReturnsFalse` | Only `Users[i].Password` paths | `false` |
| `HasUserFailure_WhenUserPasswordPresent_ReturnsTrue` | Paths include `Users[3].Password` | `true` |
| `HasUserFailure_WhenOnlyGraphSecret_ReturnsFalse` | Only `GraphApi.ClientSecret` | `false` |

---

### EmailValidation — ConfigTool (`ConfigTool/EmailValidationTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `IsValidRecipient_ValidAddresses_ReturnTrue` (Theory) | `jane.doe@contoso.com`, `ops@corp.com`, `a.b.c@sub.domain.co.uk`, spaced input | `true` |
| `IsValidRecipient_InvalidAddresses_ReturnFalse` (Theory) | empty/whitespace/null, `not-an-email`, `missing@domain` (no TLD dot), display-name form | `false` |

---

### DotNetRuntimeCheck — ConfigTool (`ConfigTool/DotNetRuntimeCheckTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `GetInstalledMajors_VersionFolders_ReturnsMajors` | shared\Microsoft.NETCore.App\{8.0.16, 6.0.36} | Majors {8, 6} |
| `GetInstalledMajors_PreviewFolder_ParsesMajor` | "9.0.0-preview.5…" folder | Major 9 (suffix stripped) |
| `GetInstalledMajors_GarbageFolderNames_AreIgnored` | Non-version folder next to 8.0.16 | Only major 8 |
| `GetInstalledMajors_MissingFrameworkFolder_ReturnsEmpty` | No shared folder | Empty |
| `GetInstalledMajors_OtherFrameworkOnly_ReturnsEmpty` | Only WindowsDesktop.App present | Empty for NETCore.App query |
| `IsServiceRuntimeInstalled_OnThisMachine_ReturnsTrue` | Real machine running the tests | `true` |

---

### MailFolderReader — ConfigTool Messages page (`ConfigTool/MailFolderReaderTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `ReadFolder_MissingDirectory_ReturnsEmpty` | Directory does not exist | Empty list, no exception |
| `ReadFolder_MapsAllFields` | Full meta.json (incl. LastAttemptAt/LastError/NextRetryAt) | All row fields mapped, UTC → local time, `Attempts = "2"` |
| `ReadFolder_MetaWithoutOptionalFields_MapsToEmptyValues` | Meta without the optional fields | Nulls/empty strings, `Attempts = "0/3"` |
| `ReadFolder_SortsByReceivedAtDescending` | Three entries out of order | Newest first |
| `ReadFolder_SentMessage_AttemptsIncludeTheSuccessfulTry` | Sent with `RetryCount` 0 / 2 | Attempts `"1"` / `"3"` (successful try counted) |
| `ReadFolder_CorruptFile_IsSkipped` | One valid + one invalid JSON file | Valid entry returned, corrupt file ignored |
| `ReadFolder_MoreThanMaxEntries_ReturnsNewestCapped` | MaxEntries + 5 files | Capped at MaxEntries, newest kept, oldest dropped |

---

### ConfigToolLog — ConfigTool diagnostic log (`ConfigTool/ConfigToolLogTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `CreateLogger_ValidDirectory_WritesExceptionTypeAndStackTrace` | Error logged with an exception carrying a real stack trace | `configtool-*.log` created; contains the `[Source] Context` prefix, exception type and the throwing stack frame |
| `CreateLogger_DirectoryPathIsAFile_ReturnsNullInsteadOfThrowing` | Log "directory" path is an existing file | Returns `null` (silent no-op) — diagnostics must never break the UI |
| `IsNewSignature_RepeatedAndChangedSignatures_SuppressesOnlyExactRepeats` | Same signature repeated, changed, then repeated again | Logged, suppressed, logged, logged — periodic 5 s checks report a recurring failure once, without swallowing new ones |

---

## Live Tests — `GraphMailer.Tests.Live` (opt-in, real M365 tenant)

> These tests talk to a **real Microsoft 365 test tenant** and are skipped unless credentials
> are configured. Credentials live **outside the repository**: .NET user secrets
> (id `GraphMailer.Tests.Live`, populate via `tools\set-live-test-secrets.ps1`),
> a gitignored `livesettings.local.json`, or `GRAPHMAILER_LiveTests__*` environment
> variables (CI). All test mail stays inside the tenant (SenderAddress → RecipientAddress).

### Graph Delivery (`GraphDeliveryLiveTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `SmallMessage_IsDelivered_ViaSendMail` | Message without large attachment | Delivered via `sendMail` (Mail.Send) |
| `LargeAttachment_IsDelivered_ViaUploadSession` | 3.5 MB attachment | Delivered via draft + upload session (Mail.ReadWrite) |
| `FidelityMessage_WithHeadersImportanceAndInlineImage_IsDelivered` | Message-ID, In-Reply-To/References (MAPI 0x1042/0x1039), custom x-header, high importance, inline CID image | Graph accepts the full fidelity payload (a rejection would break every relayed message carrying these) |
| `UnknownSender_IsRejected_ByGraph` | Nonexistent sender mailbox | `GraphDeliveryException` with 404/ErrorInvalidUser and `IsPermanent == true` (fail-fast classification verified against real Graph) |
| `AliasSender_IsDelivered_WhenResolvedToUserId` | Alias as From, resolved object id as user key (requires `LiveTests:SenderAlias`) | Resolves and delivers |

### Graph Connectivity (`GraphConnectivityLiveTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `ProbeAsync_AcquiresToken_WithRequiredRoles` | Health probe with configured credentials | Token acquired; `roles` claim contains Mail.Send, Mail.ReadWrite, User.Read.All |

### Sender Directory (`SenderDirectoryLiveTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `GetAllUsersAsync_ReturnsTheSenderMailbox` | Full tenant user sync (User.Read.All) | Sender mailbox present in directory |
| `FindBySmtpAddressAsync_KnownAddress_ReturnsUser` | On-demand lookup of the sender address | User with object id returned |
| `FindBySmtpAddressAsync_UnknownAddress_ReturnsNull` | Lookup of a nonexistent address | `null` |

---

## Integration Tests — `GraphMailer.Tests.Integration`

> All SMTP integration tests run sequentially under the `"SmtpIntegration"` xUnit collection to avoid `Directory.SetCurrentDirectory` race conditions.

---

### Service Lifecycle (`ServiceLifecycleTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Host_StartsAndStops_WithoutException` | Full `IHost` start + stop cycle | No exception |
| `Host_StopAsync_CompletesWithinTimeout` | Stop with 5 s cancellation token | Completes within timeout |
| `Host_MultipleStartStop_DoesNotThrow` | 3 consecutive start/stop cycles | No exception in any cycle |

---

### SMTP Relay — Basic Auth & TLS (`Smtp/SmtpRelayTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `PlainSmtp_NoAuthRequired_NoCredentials_AcceptsMail` | Plain SMTP, auth not required, no credentials | Mail accepted |
| `PlainSmtp_ValidCredentials_AcceptsMail` | Plain SMTP, valid credentials supplied | Mail accepted |
| `PlainSmtp_WrongCredentials_RejectsSubsequentMail` | Wrong password → `Auth:Failed` flag set | Subsequent `MAIL FROM` rejected with `SmtpCommandException` |
| `PlainSmtp_FailedThenSuccessfulAuthOnSameConnection_AcceptsMail` | Wrong password, then correct password on the SAME connection | Mail accepted (regression: the `Auth:Failed` flag is cleared by a successful re-authentication) |
| `PlainSmtp_AuthRequired_NoCredentials_RejectsMail` | Auth required, no credentials | `ServiceNotAuthenticatedException` |
| `PlainSmtp_AuthRequired_ValidCredentials_AcceptsMail` | Auth required, correct credentials | Mail accepted |
| `IpBlocking_AfterExceedingFailureThreshold_RejectsMail` | 3 failed auth attempts, threshold = 3 | Next `MAIL FROM` from same IP rejected |
| `StartTls_NoAuthRequired_NoCredentials_AcceptsMail` | STARTTLS, auth not required | Mail accepted over encrypted channel |
| `StartTls_ValidCredentials_AcceptsMail` | STARTTLS + valid credentials | Mail accepted |
| `ImplicitTls_NoAuthRequired_NoCredentials_AcceptsMail` | Implicit TLS (`SslOnConnect`), auth not required | Mail accepted |
| `ImplicitTls_ValidCredentials_AcceptsMail` | Implicit TLS + valid credentials | Mail accepted |

---

### SMTP Relay — IP & Address Filtering (`Smtp/SmtpFilterTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `IpWhitelist_ClientIpInWhitelist_AcceptsMail` | `127.0.0.1` in IP whitelist | Mail accepted |
| `IpWhitelist_ClientIpNotInWhitelist_RejectsMail` | Whitelist set to `10.0.0.1`; client is `127.0.0.1` | `MAIL FROM` rejected |
| `IpWhitelist_CidrInWhitelist_AcceptsMail` | Whitelist CIDR `127.0.0.0/8` covers `127.0.0.1` | Mail accepted |
| `IpBlacklist_ClientIpBlacklisted_RejectsMail` | `127.0.0.1` in IP blacklist | `MAIL FROM` rejected |
| `IpBlacklist_CidrCoversClientIp_RejectsMail` | Blacklist CIDR `127.0.0.0/8` covers `127.0.0.1` | `MAIL FROM` rejected |
| `IpBlacklist_OtherIpBlacklisted_AcceptsMail` | Only `10.0.0.1` blacklisted; client is `127.0.0.1` | Mail accepted |
| `SenderFilter_AllowedSenders_MatchingAddress_AcceptsMail` | `sender@example.com` on allowed-senders list | `MAIL FROM` accepted |
| `SenderFilter_AllowedSenders_NonMatchingAddress_RejectsMail` | Sender not on allowed-senders list | `MAIL FROM` rejected |
| `SenderFilter_BlockedSenders_MatchingAddress_RejectsMail` | Sender on blocked-senders list | `MAIL FROM` rejected |
| `SenderFilter_AllowedDomainWildcard_MatchingSender_AcceptsMail` | `@example.com` wildcard matches `sender@example.com` | `MAIL FROM` accepted |
| `SenderFilter_AllowedDomainWildcard_OtherDomain_RejectsMail` | `@example.com` wildcard, sender is `other@other.com` | `MAIL FROM` rejected |
| `RecipientFilter_AllowedRecipients_MatchingAddress_AcceptsMail` | `recipient@example.com` on allowed-recipients list | `RCPT TO` accepted |
| `RecipientFilter_AllowedRecipients_NonMatchingAddress_RejectsMail` | Recipient not on allowed-recipients list | `RCPT TO` rejected |
| `RecipientFilter_BlockedRecipients_MatchingAddress_RejectsMail` | Recipient on blocked-recipients list | `RCPT TO` rejected |
| `PerUserFromRestrictions_AllowedAddress_AcceptsMail` | Authenticated user sends from address in their `FromRestrictions` | `MAIL FROM` accepted |
| `PerUserFromRestrictions_ForbiddenAddress_RejectsMail` | Authenticated user sends from address outside their `FromRestrictions` | `MAIL FROM` rejected |
| `PerUserFromRestrictions_DomainWildcard_AllowedAddress_AcceptsMail` | `FromRestrictions` contains `@example.com`; user sends from `carol@example.com` | `MAIL FROM` accepted |
| `NullReversePath_MailFromEmpty_IsAcceptedAndQueued` | Raw SMTP session with `MAIL FROM:<>` (bounce/DSN sender, RFC 5321 §4.5.5) | 250 at MAIL FROM/RCPT/DATA; message lands in the queue (MailKit cannot send an empty reverse path, hence raw sockets) |

---

### SMTP Relay — TLS Certificate Fallback (`Smtp/SmtpCertificateFallbackTests.cs`)

> These tests document and verify the intentional security trade-off: when no certificate is found, the endpoint degrades to plain SMTP rather than refusing to start (see `SmtpRelayService.cs`). With `Certificate.FailClosed = true` the fallback is disabled — the listener is not started at all.

| Test | Scenario | Expected result |
|---|---|---|
| `StartTls_MissingCertificate_TlsRequiringClient_CannotConnect` | StartTLS mode, no cert; client uses `SecureSocketOptions.StartTls` | `ConnectAsync` throws (STARTTLS not advertised in EHLO) |
| `StartTls_MissingCertificate_PlainClient_CanConnectAndSend` | StartTLS mode, no cert; client uses plain SMTP | Mail accepted (endpoint degraded to plain) |
| `StartTls_MissingCertificate_OptionalTlsClient_ConnectsInPlain` | StartTLS mode, no cert; client uses `StartTlsWhenAvailable` | Connection succeeds but `client.IsSecure == false` (unencrypted — security risk) |
| `ImplicitTls_MissingCertificate_TlsClient_CannotConnect` | Implicit TLS mode, no cert; client uses `SslOnConnect` | `ConnectAsync` throws (TLS handshake fails against plain-text server) |
| `ImplicitTls_MissingCertificate_PlainClient_CanConnectAndSend` | Implicit TLS mode, no cert; client uses plain SMTP | Mail accepted (endpoint degraded to plain) |
| `StartTls_MissingCertificate_FailClosed_ListenerDoesNotStart` | StartTLS mode, no cert, `Certificate.FailClosed = true` | Port stays closed — `ConnectAsync` fails; no plain fallback |
| `StartTls_WithCertificate_FailClosed_ListenerStartsNormally` | StartTLS mode, cert present, `FailClosed = true` | STARTTLS works normally (`client.IsSecure == true`); fail-closed only bites when the cert is missing |

---

### SMTP Relay — Sender Validation (`Smtp/SmtpSenderValidationTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `ValidSender_IsAccepted` | Scripted directory returns `Valid` | Message accepted |
| `UnknownSender_IsRejectedAtMailFrom` | Scripted directory returns `Unknown` | `SmtpCommandException` (550) at MAIL FROM, nothing queued |
| `IndeterminateValidation_FailOpen_Accepts` | `Indeterminate`, `FailClosed=false` | Message accepted (fail-open) |
| `IndeterminateValidation_FailClosed_Rejects` | `Indeterminate`, `FailClosed=true` | `SmtpCommandException` at MAIL FROM |
| `ValidationDisabled_UnknownSender_IsAccepted` | Feature off, directory would reject | Message accepted — toggle wins |

---

### SMTP Relay — Size Limit (`Smtp/SmtpSizeLimitTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `SizeLimit_MessageWithinLimit_AcceptsMail` | Server configured with 1 KB limit; message body = 100 bytes | Mail accepted |
| `SizeLimit_MessageExceedsLimit_RejectsMail` | Server configured with 1 KB limit; message body = 5 KB | `SmtpCommandException` (server rejects DATA) |
| `SizeLimit_Advertised_InEhloCapabilities` | Server configured with 1 KB limit; client reads EHLO | `SmtpCapabilities.Size` flag present; `client.MaxSize == 1024` |

---

### MetricsService (`Infrastructure/Metrics/MetricsServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `Constructor_CreatesDataDirectory` | Service constructed with a temp `BasePath` | `data/` directory is created |
| `RecordEmailReceivedAsync_Enabled_InsertsRow` | Metrics enabled; record email received | DB file exists; no exception |
| `RecordEmailSentAsync_Enabled_InsertsRow` | Metrics enabled; record email sent | No exception |
| `RecordEmailFailedAsync_Enabled_InsertsRow` | Metrics enabled; record email failed | No exception |
| `RecordEmailQueuedAsync_Enabled_InsertsRow` | Metrics enabled; record email queued | No exception |
| `RecordPerfMetricAsync_Enabled_InsertsRow` | Metrics enabled; record perf metric | No exception |
| `RecordEmailReceivedAsync_Disabled_DoesNotInsert` | `Enabled = false`; record email received | No exception (silently skipped) |
| `CleanupOldRecordsAsync_RemovesExpiredRows` | `RetentionDays = 0`; cleanup called | Completes without exception |
| `RecordEmailReceivedAsync_Enabled_StoresRecipientsAndSubject` | Metrics enabled; record with two recipients and a subject | `to_addrs` column contains comma-separated addresses; `subject` column contains the subject |
| `RecordEmailSentAsync_Enabled_StoresRecipientsAndSubject` | Metrics enabled; record sent with one recipient and subject | `to_addrs` and `subject` columns in DB match the supplied values |
| `RecordEmailReceivedAsync_StoresClientIp_ForTopHostsReport` | Record received with `clientIp = 203.0.113.9` | `client_ip` column holds the IP (powers the report's top-sending-hosts table) |
| `Constructor_FreshDb_StampsCurrentSchemaVersion` | New database created | `PRAGMA user_version` == `MetricsService.SchemaVersion` |
| `Constructor_OldDbWithoutClientIp_MigratesColumnAndStampsVersion` | Pre-versioning DB (no `client_ip`, `user_version` 0) | `client_ip` column added by the migration; `user_version` stamped to current |

---

### AdminNotificationService (`Services/AdminNotificationServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `NotifyCertificateExpiring_Disabled_DoesNotSend` | `Enabled = false` | `SendNotificationAsync` not called |
| `NotifyCertificateExpiring_Enabled_Sends` | Notifications enabled, cert expiring | `SendNotificationAsync` called with subject containing `"expiring"` |
| `NotifyCertificateExpired_Enabled_Sends` | Notifications enabled, cert expired | `SendNotificationAsync` called with subject containing `"EXPIRED"` |
| `NotifyLowDiskSpace_Enabled_Sends` | Notifications enabled, low disk | `SendNotificationAsync` called with subject containing `"disk"` |
| `NotifyGraphApiError_Enabled_Sends` | Notifications enabled, Graph API error | `SendNotificationAsync` called with subject containing `"Graph API"` |
| `NotifyGraphApiRestored_Enabled_Sends` | Notifications enabled, Graph API restored | `SendNotificationAsync` called with subject containing `"restored"` |
| `NotifyPortOutage_Enabled_Sends` | Notifications enabled, port 2525 down | `SendNotificationAsync` called with subject containing `"2525"` |
| `NotifyPortRestored_Enabled_Sends` | Notifications enabled, port 2525 recovered | `SendNotificationAsync` called with subject containing `"2525"` and `"restored"` |
| `NotifyCertificateExpiring_NoSenderAddress_DoesNotSend` | `SenderAddress = null` | `SendNotificationAsync` not called (warning logged) |
| `NotifyIpBlocked_BelowThreshold_DoesNotSend` | 4 IP-blocked events; threshold = 5 | `SendNotificationAsync` not called |
| `NotifyEmailDeliveryFailed_Disabled_DoesNotQueue` | `EmailDeliveryFailed.Enabled = false` | Timer not started; Graph API not called |
| `NotifyBackupResult_Success_Sends_WithSucceededSubject` | Backup succeeded | `SendNotificationAsync` with subject containing `"backup succeeded"` and the file in the body |
| `NotifyBackupResult_Failure_Sends_WithFailedSubject` | Backup failed | Subject contains `"FAILED"`, body contains the reason |
| `NotifyBackupResult_TypeDisabled_DoesNotSend` | `BackupResult.Enabled = false` | `SendNotificationAsync` not called |
| `NotifyUpdateAvailable_DefaultOptions_DoesNotSend` | Admin notifications enabled, `UpdateAvailable` at its default | `SendNotificationAsync` not called (the type is opt-in) |
| `NotifyUpdateAvailable_TypeEnabled_Sends_WithVersionsAndUrl` | `UpdateAvailable.Enabled = true` | Subject contains `"Update available"` + latest version; body contains both versions and the release URL |
| `NotifyUpdateAvailable_MasterDisabled_DoesNotSend` | `Enabled = false`, type enabled | `SendNotificationAsync` not called |

---

### AdminNotificationService — NDR (`Services/AdminNotificationServiceNdrTests.cs`)

> NDRs are written to the service's own mail queue (with `IsNotification = true`) instead of
> being sent one-shot via Graph, so they inherit the queue processor's full retry schedule.

| Test | Scenario | Expected result |
|---|---|---|
| `SendNdrAsync_Disabled_QueuesNothing` | `NdrOptions.Enabled = false` | Nothing written to the mail queue |
| `SendNdrAsync_NotifySender_QueuesNdrToOriginalSender` | `NotifySender = true`, `NotifyAdmin = false` | One queued mail; recipient is original sender; subject contains `"Undeliverable"`; `IsNotification == true` |
| `SendNdrAsync_NotifyAdmin_QueuesNdrToAdminRecipients` | `NotifySender = false`, `NotifyAdmin = true` | One queued mail; recipient is admin address; `IsNotification == true` |
| `SendNdrAsync_BothEnabled_QueuesTwoMails` | `NotifySender = true`, `NotifyAdmin = true` | Exactly two queued mails (sender + admin) |
| `SendNdrAsync_EmptyFrom_SkipsSenderNdr` | `meta.From = ""` | Nothing queued (no valid sender address to notify) |
| `SendNdrAsync_FromEqualsAdminSender_SkipsSenderNdrToPreventLoop` | `meta.From == SenderAddress` | Nothing queued (loop prevention guard) |
| `SendNdrAsync_NoSenderAddress_QueuesNothing` | `AdminNotifications.SenderAddress = null` | Nothing queued; warning logged |
| `SendNdrAsync_QueuedNdrIsParseableEml` | NDR queued for the sender | The queued `.eml` is valid MIME (parseable by MimeKit) with the expected subject, body and From |

---

### CertificateMonitoringService (`Services/CertificateMonitoringServiceTests.cs`)

| Test | Scenario | Expected result |
|---|---|---|
| `ExecuteAsync_Disabled_DoesNotCheckCertificate` | `Enabled = false` | `ICertificateLoader.LoadCertificate` never called |
| `ExecuteAsync_NoCertificate_DoesNotNotify` | No cert configured (loader returns null) | Neither `NotifyCertificateExpiringAsync` nor `NotifyCertificateExpiredAsync` called |
| `ExecuteAsync_ExpiringCertificate_NotifiesExpiringSoon` | Self-signed cert expiring in 5 days; threshold = 14 days | `NotifyCertificateExpiringAsync` called |
| `ExecuteAsync_ExpiredCertificate_NotifiesExpired` | Self-signed cert expired 1 day ago | `NotifyCertificateExpiredAsync` called |

---

### GitHubUpdateChecker (`Services/UpdateCheck/GitHubUpdateCheckerTests.cs`)

Parses the GitHub `/releases/latest` response and compares the release tag (`v<FileVersion>`) against the running version.

| Test | Scenario | Expected result |
|---|---|---|
| `Evaluate_NewerRelease_UpdateAvailable` | Latest tag `v1.3.0.210`, running `1.2.0.196` | `Success`, `UpdateAvailable == true`, `LatestVersion == "1.3.0.210"` |
| `Evaluate_SameVersion_NoUpdate` | Latest tag equals the running version | `UpdateAvailable == false` |
| `Evaluate_OlderRelease_NoUpdate` | Running build newer than the latest release (dev build) | `UpdateAvailable == false` |
| `Evaluate_TagWithoutVPrefix_IsParsed` | Tag `2.0.0.300` without `v` prefix | Parsed and compared normally |
| `Evaluate_NewerBuildOfSameSemVer_IsAnUpdate` | Hotfix tag `v1.2.0.200` vs running `1.2.0.196` | `UpdateAvailable == true` (full four-part compare) |
| `Evaluate_ParsesUrlNameAndPublishedDate` | Full release JSON | `ReleaseUrl`, `ReleaseName`, `PublishedUtc` extracted |
| `Evaluate_UnparseableTag_IsError` | Tag `latest-stable` | Error result naming the tag |
| `Evaluate_MissingTagName_IsError` | Response without `tag_name` | Error result mentioning `tag_name` |
| `Evaluate_InvalidJson_IsError` | Malformed JSON body | Error result (`Invalid release response`) |
| `CheckAsync_SuccessResponse_ReturnsSuccess_AndSendsUserAgent` | Fake handler returns 200 + release JSON | Success result; request carries a `User-Agent` header and targets `api.github.com` |
| `CheckAsync_HttpErrorStatus_IsErrorResult` | Fake handler returns 404 | Error result containing the status code; no exception |
| `CheckAsync_NetworkFailure_IsErrorResult_NeverThrows` | Handler throws `HttpRequestException` | Error result with the exception message; never throws |

---

### UpdateCheckService (`Services/UpdateCheck/UpdateCheckServiceTests.cs`)

Weekly opt-in check scheduler: persists the cadence in `data\update-status.json`, honours the ConfigTool "check now" request file, and mails the admin once per new version.

| Test | Scenario | Expected result |
|---|---|---|
| `IsCheckDue_NoStatusFile_IsDue` | No status file yet | Due — the first check runs right after enabling |
| `IsCheckDue_NextCheckInFuture_IsNotDue` | Persisted `NextCheckUtc` 3 days ahead | Not due — a service restart within the weekly window does not re-check |
| `IsCheckDue_NextCheckPassed_IsDue` | Persisted `NextCheckUtc` in the past | Due |
| `RunCheck_Success_WritesStatus_WithWeeklyNextCheck` | Successful up-to-date check | Status file written (versions, no error); `NextCheckUtc ≈ now + 7 days` |
| `RunCheck_UpToDate_DoesNotNotify` | Latest equals running version | `NotifyUpdateAvailableAsync` not called |
| `RunCheck_UpdateAvailable_NotifiesOnce_AndPersistsNotifiedVersion` | Two weekly checks find the same new release | Exactly one notification; `LastNotifiedVersion` persisted |
| `RunCheck_EvenNewerRelease_NotifiesAgain` | A second, even newer release appears | One notification per distinct version |
| `ConsumeCheckRequest_FilePresent_ReturnsTrue_AndDeletesIt` | ConfigTool dropped `update-check.request` | Returns `true` once and removes the file (one-shot) |
| `RunCheck_Failure_KeepsPreviousResult_SetsError_RetriesTomorrow_NoMail` | Check fails after an earlier success | `LastError` set; previous release info and `LastNotifiedVersion` preserved; `NextCheckUtc ≈ now + 1 day`; no mail |

---

### Config schema versioning (`Infrastructure/Config/ConfigSchemaTests.cs`, `ConfigServiceTests.cs`)

`ConfigSchema` / `ConfigMigrator` migrate `graphmailer.json` forward to the current schema version.

| Test | Scenario | Expected result |
|---|---|---|
| `ReadVersion_Absent_IsZero` | No `SchemaVersion` key | `0` (pre-versioning) |
| `ReadVersion_Present_IsValue` | `SchemaVersion = 3` | `3` |
| `Migrate_V0_RemovesObsoleteRetryKeys_AndStampsVersion` | v0 doc with `MailQueue.MaxRetries`/`RetryDelaySeconds` | Obsolete keys removed, unrelated keys kept, version stamped to current |
| `Migrate_V1_ToCurrent_IsAdditiveOnly_ContentUnchangedExceptVersion` | v1 doc (v2 only added `Certificate.FailClosed`) | Version stamped to current; existing content untouched; the absent key stays absent (binder default applies) |
| `Migrate_V2_ToV3_IsAdditiveOnly_ContentUnchangedExceptVersion` | v2 doc (v3 only added `UpdateCheck.Enabled` + the `UpdateAvailable` notification type) | Version stamped to 3; existing content untouched; the absent keys stay absent (binder defaults apply) |
| `Migrate_AlreadyCurrent_IsNoOp` | Doc already at current version | `false` (no change) |
| `Migrate_Idempotent` | Migrate twice | First `true`, second `false` |
| `Migrate_NewerThanBuild_LeavesFileAlone` | `SchemaVersion = Current + 1` | `false`; version left untouched |
| `MigrateFile_OldFile_Migrates_BacksUp_AndStamps` | v0 file on disk | Migrated; original backed up to `config\backups\`; file stamped to current |
| `MigrateFile_CurrentFile_IsNoOp` | File already at current version | `Migrated == false` |
| `MigrateFile_NewerFile_IsIncompatible_AndUnchanged` | File from a newer build | `Incompatible == true`; file untouched |
| `MigrateFile_MissingFile_IsNoOp` | No file | `Migrated == false` |
| `MigrateFile_InvalidJson_IsLeftAlone` | Corrupt JSON | `Migrated == false`; file untouched |
| `QuarantineIfCorrupt_InvalidJson_MovesFileAsideAndReturnsPath` | Truncated JSON on disk | File moved to `graphmailer.json.corrupt-<ts>` (content preserved); config path free so startup succeeds |
| `QuarantineIfCorrupt_ValidJson_IsNoOp` | Valid JSON | `null`; file untouched |
| `QuarantineIfCorrupt_MissingFile_IsNoOp` | No file | `null` |
| `PruneMigrationBackups_KeepsOnlyTheNewestTen` | 13 `.bak` files with staggered creation times | Oldest 3 deleted, newest 10 kept |
| `Save_StampsCurrentSchemaVersion` (ConfigServiceTests) | Save a document | Reloaded `doc.SchemaVersion == ConfigSchema.Current` |

---

### ConfigSchemaLoadTests (`Infrastructure/Config/ConfigSchemaLoadTests.cs`)

Verifies that every JSON key written by the service (`graphmailer.json`) is correctly read back into `ConfigDocument` by `ConfigService.Load()`, so ConfigTool can display and edit values without silently discarding them.

| Test | Scenario | Expected result |
|---|---|---|
| `Load_Certificate_FailClosed_AppearsInDocCertificateFailClosed` | `Certificate.FailClosed = true` in JSON | `doc.Certificate.FailClosed == true` |
| `Load_Certificate_FailClosedAbsent_DefaultsToFalse` | `Certificate` section without the key (pre-v2 config) | `doc.Certificate.FailClosed == false` |
| `Load_CertificateMonitoring_WarningThresholdDays_AppearsInDocMonitoringCertWarnDays` | `CertificateMonitoring.WarningThresholdDays = 7` in JSON | `doc.Monitoring.CertWarnDays == 7` |
| `Load_DiskSpaceMonitoring_ThresholdPercent_AppearsInDocMonitoringDiskWarnPct` | `DiskSpaceMonitoring.ThresholdPercent = 25` | `doc.Monitoring.DiskWarnPct == 25` |
| `Load_PortMonitoring_CheckIntervalMinutes_AppearsInDocMonitoringPortCheckInterval` | `PortMonitoring.CheckIntervalMinutes = 3` | `doc.Monitoring.PortCheckIntervalMinutes == 3` |
| `Load_GraphApiMonitoring_CheckIntervalMinutes_AppearsInDocMonitoringGraphCheckInterval` | `GraphApiMonitoring.CheckIntervalMinutes = 30` | `doc.Monitoring.GraphCheckIntervalMinutes == 30` |
| `Load_AdminNotifications_RecipientAddresses_AppearsInDocNotificationRecipientAddresses` | `RecipientAddresses: ["ops@corp.com"]` | `doc.Notification.RecipientAddresses` contains the address |
| `Load_AdminNotifications_SenderAddress_AppearsInDocNotificationNotifFrom` | `SenderAddress: "relay@corp.com"` | `doc.Notification.NotifFrom == "relay@corp.com"` |
| `Load_AdminNotifications_SubjectPrefix_AppearsInDocNotificationSubjectPrefix` | `SubjectPrefix: "[PROD]"` | `doc.Notification.SubjectPrefix == "[PROD]"` |
| `Load_AdminNotifications_IpBlockedAlert_Disabled_AppearsInDocNotifIpBlocked_False` | `IpBlockedAlert.Enabled = false` | `doc.Notification.NotifIpBlocked == false` |
| `Load_AdminNotifications_EmailDeliveryFailed_Disabled_AppearsInDocNotifDeliveryFailed_False` | `EmailDeliveryFailed.Enabled = false` | `doc.Notification.NotifDeliveryFailed == false` |
| `Load_AdminNotifications_CertificateExpiringWarning_Disabled_AppearsInDocNotifCertExpiring_False` | `CertificateExpiringWarning.Enabled = false` | `doc.Notification.NotifCertExpiring == false` |
| `Load_AdminNotifications_CertificateExpired_Disabled_AppearsInDocNotifCertExpired_False` | `CertificateExpired.Enabled = false` | `doc.Notification.NotifCertExpired == false` |
| `Load_AdminNotifications_LowDiskSpaceWarning_Disabled_AppearsInDocNotifDiskSpace_False` | `LowDiskSpaceWarning.Enabled = false` | `doc.Notification.NotifDiskSpace == false` |
| `Load_AdminNotifications_GraphApiConnectionError_Disabled_AppearsInDocNotifGraphDown_False` | `GraphApiConnectionError.Enabled = false` | `doc.Notification.NotifGraphDown == false` |
| `Load_AdminNotifications_PortMonitoringAlert_Disabled_AppearsInDocNotifPortDown_False` | `PortMonitoringAlert.Enabled = false` | `doc.Notification.NotifPortDown == false` |
| `Load_Server_AuthRequired_True_MapsToAuthMode_Required` | `AuthRequired: true` on server entry | `doc.Servers[0].AuthMode == "Required"` |
| `Load_Server_AuthMode_None_RoundTripsCorrectly` | `AuthMode: "None"`, `AuthRequired: false` | `doc.Servers[0].AuthMode == "None"` |
| `Load_AllMonitoringAndNotificationKeys_AllMappedToConfigDocument` | All monitoring + notification keys set | All matching `ConfigDocument` properties contain the written values |
| `Load_MailQueue_MailDir_AppearsInDocMailQueueMailDir` | `MailQueue.MailDir = "D:\MailStorage"` | `doc.MailQueue.MailDir == "D:\MailStorage"` |
| `Load_MailQueue_FailedEmailRetentionDays_AppearsInDocMailQueue` | `MailQueue.FailedEmailRetentionDays = 14` | `doc.MailQueue.FailedEmailRetentionDays == 14` |
| `Load_MailQueue_RetryPolicy_AppearsInDocMailQueue` | `MailQueue` transient/steady/expiration keys | `doc.MailQueue.{TransientRetryCount,TransientRetryIntervalSeconds,RetryIntervalSeconds,MessageExpirationHours}` match |
| `Load_Metrics_Enabled_False_AppearsInDocMetricsEnabled_False` | `Metrics.Enabled = false` | `doc.Metrics.Enabled == false` |
| `Load_Metrics_RetentionDays_AppearsInDocMetricsRetentionDays` | `Metrics.RetentionDays = 30` | `doc.Metrics.RetentionDays == 30` |
| `Load_Metrics_CleanupIntervalHours_AppearsInDocMetricsCleanupIntervalHours` | `Metrics.CleanupIntervalHours = 12` | `doc.Metrics.CleanupIntervalHours == 12` |
| `Load_Metrics_PerformanceMetrics_Enabled_False_AppearsInDocMetricsPerfMetricsEnabled_False` | `Metrics.PerformanceMetrics.Enabled = false` | `doc.Metrics.PerfMetricsEnabled == false` |
| `Load_Metrics_PerformanceMetrics_MemoryIntervalSeconds_AppearsInDocMetricsPerfMemoryIntervalSeconds` | `PerformanceMetrics.MemoryIntervalSeconds = 120` | `doc.Metrics.PerfMemoryIntervalSeconds == 120` |
| `Load_Metrics_PerformanceMetrics_CpuIntervalSeconds_AppearsInDocMetricsPerfCpuIntervalSeconds` | `PerformanceMetrics.CpuIntervalSeconds = 30` | `doc.Metrics.PerfCpuIntervalSeconds == 30` |
| `Load_Metrics_PerformanceMetrics_DiskIntervalSeconds_AppearsInDocMetricsPerfDiskIntervalSeconds` | `PerformanceMetrics.DiskIntervalSeconds = 600` | `doc.Metrics.PerfDiskIntervalSeconds == 600` |
| `Load_Backup_AllFields_AppearInDocBackup` | Full `Backup` section in JSON (freq, time, day, max, dir, email) | All map to `doc.Backup.*` |
| `Load_AdminNotifications_BackupResult_Disabled_AppearsInDocNotifBackup_False` | `BackupResult.Enabled = false` | `doc.Notification.NotifBackup == false` |
| `Load_AdminNotifications_ScheduledReport_AllFields_AppearInDocNotification` | Full `AdminNotifications.ScheduledReport` section (enabled, frequency, time, day-of-week, day-of-month) | All map to `doc.Notification.Report*` |
| `Load_UpdateCheck_Enabled_AppearsInDocMonitoringUpdateCheckEnabled` | `UpdateCheck.Enabled = true` | `doc.Monitoring.UpdateCheckEnabled == true` |
| `Load_UpdateCheck_Absent_DefaultsToDisabled` | No `UpdateCheck` section (pre-v3 config) | `doc.Monitoring.UpdateCheckEnabled == false` |
| `Load_AdminNotifications_UpdateAvailable_Enabled_AppearsInDocNotifUpdateAvailable_True` | `UpdateAvailable.Enabled = true` | `doc.Notification.NotifUpdateAvailable == true` |
| `Load_AdminNotifications_UpdateAvailable_Absent_DefaultsToDisabled` | `NotificationTypes` without `UpdateAvailable` | `doc.Notification.NotifUpdateAvailable == false` (opt-in) |

---

### ConfigSchemaBindingTests (`Infrastructure/Config/ConfigSchemaBindingTests.cs`)

Verifies that `ConfigService.Save()` writes the correct JSON keys so that `Microsoft.Extensions.Configuration` binds them correctly into the service's `*Options` classes at runtime.

| Test | Scenario | Expected result |
|---|---|---|
| `Save_CertificateFailClosed_BindsToCertificateFailClosed` | `doc.Certificate.FailClosed = true` saved | Options bound: `Certificate:FailClosed == true` |
| `Save_FailedEmailRetentionDays_BindsToMailQueueFailedEmailRetentionDays` | `doc.MailQueue.FailedEmailRetentionDays = 14` saved | Options bound: `MailQueue:FailedEmailRetentionDays == 14` |
| `Save_CertWarnDays_BindsToCertificateMonitoringWarningThresholdDays` | `doc.Monitoring.CertWarnDays = 7` saved | Options bound: `CertificateMonitoring:WarningThresholdDays == 7` |
| `Save_DiskWarnPct_BindsToDiskSpaceMonitoringThresholdPercent` | `doc.Monitoring.DiskWarnPct = 25` saved | `DiskSpaceMonitoring:ThresholdPercent == 25` |
| `Save_PortCheckIntervalMinutes_BindsToPortMonitoringCheckIntervalMinutes` | `doc.Monitoring.PortCheckIntervalMinutes = 3` saved | `PortMonitoring:CheckIntervalMinutes == 3` |
| `Save_GraphCheckIntervalMinutes_BindsToGraphApiMonitoringCheckIntervalMinutes` | `doc.Monitoring.GraphCheckIntervalMinutes = 30` saved | `GraphApiMonitoring:CheckIntervalMinutes == 30` |
| `Save_RecipientAddresses_BindToAdminNotificationsRecipientAddresses` | Recipient address in doc | `AdminNotifications:RecipientAddresses[0]` matches |
| `Save_RecipientAddressesEmpty_DisablesAdminNotifications` | Empty recipient list | `AdminNotifications:Enabled == false` |
| `Save_SubjectPrefix_BindsToAdminNotificationsSubjectPrefix` | `SubjectPrefix = "[PROD]"` | `AdminNotifications:SubjectPrefix == "[PROD]"` |
| `Save_ScheduledReport_BindsToAdminNotificationsScheduledReport` | `doc.Notification.Report*` set (Monthly, 08:30, Friday, day 5) saved | `AdminNotifications:ScheduledReport` binds to `ScheduledReportOptions` (Enabled, Frequency, TimeOfDay, DayOfWeek, DayOfMonth) |
| `Save_NotifIpBlocked_False_BindsToIpBlockedAlertEnabled_False` | `NotifIpBlocked = false` | `IpBlockedAlert:Enabled == false` |
| `Save_NotifDeliveryFailed_False_BindsToEmailDeliveryFailedEnabled_False` | `NotifDeliveryFailed = false` | `EmailDeliveryFailed:Enabled == false` |
| `Save_NotifCertExpiring_False_BindsToCertificateExpiringWarningEnabled_False` | `NotifCertExpiring = false` | `CertificateExpiringWarning:Enabled == false` |
| `Save_NotifCertExpired_False_BindsToCertificateExpiredEnabled_False` | `NotifCertExpired = false` | `CertificateExpired:Enabled == false` |
| `Save_NotifDiskSpace_False_BindsToLowDiskSpaceWarningEnabled_False` | `NotifDiskSpace = false` | `LowDiskSpaceWarning:Enabled == false` |
| `Save_NotifGraphDown_False_BindsToGraphApiConnectionErrorEnabled_False` | `NotifGraphDown = false` | `GraphApiConnectionError:Enabled == false` |
| `Save_NotifPortDown_False_BindsToPortMonitoringAlertEnabled_False` | `NotifPortDown = false` | `PortMonitoringAlert:Enabled == false` |
| `Save_AuthModeRequired_BindsToSmtpServerEntryAuthRequired_True` | `AuthMode = "Required"` | JSON entry has `AuthRequired: true` |
| `Save_AuthModeNone_BindsToSmtpServerEntryAuthRequired_False` | `AuthMode = "None"` | JSON entry has `AuthRequired: false` |
| `Save_AllMonitoringAndNotificationFields_BindCorrectlyAsServiceOptions` | All monitoring + notification fields set | All corresponding Options properties bound correctly |
| `Save_MailQueueMailDir_BindsToMailQueueOptionsMailDir` | `MailQueue.MailDir = "D:\MailStorage"` | `MailQueueOptions.MailDir == "D:\MailStorage"` |
| `Save_MailQueueRetryPolicy_BindsToMailQueueOptions` | `doc.MailQueue` transient/steady/expiration set | `MailQueueOptions.{TransientRetryCount,TransientRetryIntervalSeconds,RetryIntervalSeconds,MessageExpirationHours}` bind correctly |
| `Save_MetricsEnabled_False_BindsToMetricsOptionsEnabled_False` | `Metrics.Enabled = false` | `MetricsOptions.Enabled == false` |
| `Save_MetricsRetentionDays_BindsToMetricsOptionsRetentionDays` | `Metrics.RetentionDays = 30` | `MetricsOptions.RetentionDays == 30` |
| `Save_MetricsCleanupIntervalHours_BindsToMetricsOptionsCleanupIntervalHours` | `Metrics.CleanupIntervalHours = 12` | `MetricsOptions.CleanupIntervalHours == 12` |
| `Save_MetricsPerfEnabled_False_BindsToPerformanceMetricsOptionsEnabled_False` | `Metrics.PerfMetricsEnabled = false` | `PerformanceMetricsOptions.Enabled == false` |
| `Save_MetricsPerfMemoryIntervalSeconds_BindsToPerformanceMetricsOptionsMemoryIntervalSeconds` | `PerfMemoryIntervalSeconds = 120` | `PerformanceMetricsOptions.MemoryIntervalSeconds == 120` |
| `Save_MetricsPerfCpuIntervalSeconds_BindsToPerformanceMetricsOptionsCpuIntervalSeconds` | `PerfCpuIntervalSeconds = 30` | `PerformanceMetricsOptions.CpuIntervalSeconds == 30` |
| `Save_MetricsPerfDiskIntervalSeconds_BindsToPerformanceMetricsOptionsDiskIntervalSeconds` | `PerfDiskIntervalSeconds = 600` | `PerformanceMetricsOptions.DiskIntervalSeconds == 600` |
| `Save_DisabledUser_BindsToUserEntryEnabledFalse` | User saved with `Enabled = false` | Runtime `UserEntry.Enabled == false` (regression: flag was silently dropped) |
| `Backup_AllFields_BindToBackupOptions` | Full `Backup` section saved | `BackupOptions` bound: frequency (enum), time, day (enum), max, dir, email + recipients |
| `Backup_Password_IsWrittenEncrypted` | `Backup.Password` saved | JSON value is `ENC[...]` |
| `Save_NotifBackup_False_BindsToBackupResultEnabled_False` | `Notification.NotifBackup = false` | `AdminNotifications:NotificationTypes:BackupResult:Enabled == false` |
| `Save_UpdateCheckEnabled_BindsToUpdateCheckEnabled` | `doc.Monitoring.UpdateCheckEnabled = true` saved | Options bound: `UpdateCheck:Enabled == true` |
| `Save_NotifUpdateAvailable_True_BindsToUpdateAvailableEnabled_True` | `Notification.NotifUpdateAvailable = true` | `AdminNotifications:NotificationTypes:UpdateAvailable:Enabled == true` |
