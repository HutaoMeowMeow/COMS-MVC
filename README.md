# COMS MVC - Canal Obstruction Maintenance System (Flood Monitoring)

A web-based canal flood monitoring and management system built with **ASP.NET Core MVC (.NET 10)**, **Entity Framework Core 9 + PostgreSQL**, **ASP.NET Core Identity**, and **SignalR** for real-time updates.

> Reference guide: every model, controller, view/UI URL, role access, seeded account, migration, environment setting, and test area is documented below.

Base URL (development): `http://localhost:5070`

---

## 1. System Overview

COMS centralizes:

- Canal inventory with map coordinates + water-level thresholds
- Sensor registry + sensor readings (water level, flow rate, debris, turbidity, temperature)
- Automatic obstruction alerts with severity + assignment, plus per-type duplicate suppression
- Flood risk assessments (rule-based score per canal)
- Community reports from residents (with validated photo upload, content-relevance gate, and advisory authenticity analysis)
- Announcements with target audience (with validated image upload)
- Flood News from public keyless RSS feeds (GDACS + Philippine outlets)
- Resident help center / support tickets with real-time chat
- Two-factor authentication (TOTP authenticator app + recovery codes + trusted devices)
- Real-time notifications via SignalR
- Role-based dashboards for 5 operational roles, plus a Customer Service workspace

### Tech Stack

| Layer     | Technology / File                                              |
| --------- | -------------------------------------------------------------- |
| Framework | ASP.NET Core MVC 10 / .NET 10 — `Program.cs`, `COMS MVC.csproj` |
| Database  | PostgreSQL 17+ via `Npgsql.EntityFrameworkCore.PostgreSQL` 9.x |
| ORM       | EF Core 9 — `Data/ApplicationDbContext.cs`, `Migrations/`      |
| Auth      | ASP.NET Core Identity (int keys), lockout 5 attempts / 5 min, TOTP 2FA |
| Real-time | SignalR + JSON — `/monitoringhub` and `/supporthub`             |
| Frontend  | Bootstrap 5, jQuery, Chart.js, Leaflet.js (OpenStreetMap)      |
| Images    | SkiaSharp 2.88.2 (validation, relevance, EXIF Software tag)    |
| Uploads   | Disk storage under `wwwroot/uploads/`                          |
| Tests     | xUnit + SQLite in-memory (`COMS.Tests/`)                       |
| Hosting   | Dockerfile + Railway/container env conventions                 |

### Default Routing

Conventional route in `Program.cs`:

```text
{controller=Dashboard}/{action=Index}/{id?}
```

Example: `CanalsController.Details(int id)` → `/Canals/Details/5` → `Views/Canals/Details.cshtml`

Cookie settings in `Program.cs`:

- Login: `/Account/Login`
- Logout: `/Account/Logout` (POST)
- Access denied: `/Dashboard/AccessDenied`
- 404 redirects to: `/Dashboard/HttpError`

---

## 2. Project Structure (Where Files Live)

```text
COMS-MVC/
├── Program.cs                    # Startup: DB, Identity/2FA, DI, SignalR, rate limiting, routing, seeder
├── appsettings.json              # Connection string + feature options (no secrets committed)
├── Data/
│   └── ApplicationDbContext.cs   # 13 domain DbSets + Identity + Fluent API relationships
├── Models/                       # Entities + ViewModels (see §3)
├── Controllers/                  # 14 controllers (see §4)
├── Views/                        # Razor pages per controller + Shared layout (see §5)
├── Helpers/                      # PhTime, RateThrottle, TrustedDeviceCookie, validation helpers
├── Services/
│   ├── DataSeeder.cs             # Roles, 4 demo users, lookup options (no demo canals/sensors)
│   ├── SensorService.cs          # Sensor + simulated reading helpers
│   ├── AlertService.cs           # Threshold detection + per-type alert dedup
│   ├── AlertRetentionService.cs  # Periodic alert retention sweep
│   ├── FloodRiskService.cs       # Rule-based risk scoring
│   ├── FloodRiskRetentionService.cs
│   ├── NotificationService.cs    # Inbox + unread count + SignalR dispatch
│   ├── SensorSimulationService.cs# Background simulated readings (off by default)
│   ├── ChartDataDownsampler.cs   # Bounded, peak-preserving chart sampling
│   ├── CebuBoundaryService.cs    # Server-side report-area polygon check
│   ├── EmailService.cs           # Brevo HTTPS email (OTP + password reset)
│   ├── OtpService.cs             # Registration OTP lifecycle
│   ├── FloodNewsService.cs       # GDACS + PH RSS aggregation, cache/stale states
│   ├── FloodRelevance.cs         # Flood-news relevance/location classifier
│   ├── ImageUploadValidator.cs   # JPEG/PNG + magic bytes + decode + pixel cap
│   ├── ImageVerificationService.cs # Advisory authenticity heuristic + EXIF Software tag
│   ├── ImageRelevanceService.cs  # Offline canal/flood photo relevance heuristic
│   ├── TextModerationService.cs  # Report/ticket text moderation
│   ├── TrustedDeviceService.cs   # Hashed 2FA trusted-device tokens
│   ├── TrustedDeviceCleanupService.cs
│   └── LookupService.cs          # Editable dropdown options
├── Hubs/
│   ├── MonitoringHub.cs          # user, role, canal groups
│   └── SupportHub.cs             # support ticket chat groups
├── Migrations/                   # 11 EF Core PostgreSQL migrations (see §8.1)
├── COMS.Tests/                   # xUnit test suite (see §12)
├── wwwroot/
│   ├── images/COMS-LOGO.png      # System logo (transparent variant available)
│   ├── js/water-trend.js         # Real-reading dashboard trend chart
│   ├── uploads/announcements/    # Created at runtime — announcement images
│   └── uploads/reports/          # Created at runtime — community report photos
├── Dockerfile / .dockerignore    # Container build (see §13)
├── COMS-LOGO.png                 # Source logo file (project root, copied into wwwroot/images/)
└── COMS MVC.slnx / COMS MVC.csproj
```

---

## 3. Models (Database Entities)

Location: `Models/` — Tables created via `Migrations/` — DbSets in `Data/ApplicationDbContext.cs`.

`*` = `[Required]` in code.

### 3.1 ApplicationUser — `Models/User.cs` → `AspNetUsers`

Identity user (int key). Extra fields: `FullName*`, `Barangay`, `City`, `Address`, `CreatedAt`. Standard Identity 2FA fields (`TwoFactorEnabled`, authenticator key, recovery codes) are used for TOTP login.

Related tables: `AspNetRoles`, `AspNetUserRoles`, claims/logins, `TrustedDevices`.

Has many: CommunityReports, Notifications, PostedAnnouncements, SupportTickets, SupportMessages, TrustedDevices.

### 3.2 Canal — `Models/Canal.cs` → `Canals`

Master canal record.

| Field | Notes |
| ----- | ----- |
| `CanalId` | PK |
| `CanalName*`, `Location*`, `Barangay*`, `City*` | Required text |
| `Latitude`, `Longitude` | Map position (Leaflet) — must be inside Cebu (lat 9.30–11.45, lng 123.20–124.60), enforced in `CanalsController` |
| `Length`, `Width`, `Depth` | Dimensions (m) |
| `NormalWaterLevel`, `WarningWaterLevel`, `CriticalWaterLevel` | Thresholds (m) |
| `Status`, `CreatedAt` | Defaults to `Normal` |

Parent of: Sensors, SensorReadings, Alerts, CommunityReports, FloodRiskAssessments.

### 3.3 Sensor — `Models/Sensor.cs` → `Sensors`

| Field | Notes |
| ----- | ----- |
| `SensorId` | PK |
| `SensorCode*` | Unique |
| `SensorType` | e.g. WaterLevel / Flow |
| `CanalId` | FK → Canals |
| `Latitude`, `Longitude` | Sensor position — must be inside Cebu, enforced in `SensorsController.ValidateSensorAsync` |
| `Status` | Defaults to `Online` |
| `LastReading`, `LastCommunication` | Timestamps |

Has many: SensorReadings.

### 3.4 SensorReading — `Models/SensorReading.cs` → `SensorReadings`

Time-series reading. Indexed on `RecordedAt`.

| Field | Notes |
| ----- | ----- |
| `SensorReadingId` | PK |
| `SensorId`, `CanalId` | FKs |
| `WaterLevel`, `FlowRate`, `DebrisLevel`, `Turbidity`, `Temperature` | Measurements |
| `RecordedAt` | Timestamp |
| `IsSimulated` | Defaults to `true` |

Can trigger ObstructionAlerts.

### 3.5 ObstructionAlert — `Models/ObstructionAlert.cs` → `ObstructionAlerts`

| Field | Notes |
| ----- | ----- |
| `ObstructionAlertId` | PK |
| `CanalId` | FK |
| `AlertType`, `ObstructionType` | Classification |
| `Severity` | Low / Medium / High / Critical |
| `Title*`, `Description` | Details |
| `WaterLevel` | Level at detection |
| `SensorReadingId?` | Nullable FK (SetNull on delete) |
| `DetectedAt` | Indexed |
| `Status` | Active / Acknowledged / In Progress / Resolved |
| `AssignedToUserId?` | FK → AspNetUsers (Maintenance) |
| `ResolutionNotes`, `ResolvedAt?` | Closure info |

Raised by `AlertService` or manually.

### 3.6 CommunityReport — `Models/CommunityReport.cs` → `CommunityReports`

Resident-submitted concern with optional photo.

| Field | Notes |
| ----- | ----- |
| `CommunityReportId` | PK |
| `UserId` | FK → AspNetUsers (Resident) |
| `CanalId?` | Nullable FK → Canals (resident may report a physical location without a known canal) |
| `ReportType`, `Title*`, `Description` | Content (description capped at 2000 chars) |
| `PhotoPath` | e.g. `/uploads/reports/abc.jpg` |
| `Location` | Landmark / street detail (free text) |
| `Latitude?`, `Longitude?` | Map pin from resident picker — must be inside the Cebu report-area polygons (`wwwroot/data/cebu-report-area.geojson`, enforced server-side by `CebuBoundaryService`) |
| `ImageRelevanceStatus`, `ImageRelevanceConfidence`, `ImageRelevanceReason`, `ImageSuggestedCategory` | Synchronous relevance gate result (Relevant accepted; Irrelevant/Uncertain rejected before save) |
| `ImageVerificationStatus`, `ImageVerificationConfidence`, `ImageVerificationReason`, `ImageVerificationAnalyzedAt` | Advisory authenticity result, analyzed after save in the background |
| `Status` | Pending / Under Review / Resolved |
| `CreatedAt`, `UpdatedAt` | Timestamps |

### 3.7 FloodRiskAssessment — `Models/FloodRiskAssessment.cs` → `FloodRiskAssessments`

Computed by `FloodRiskService` per canal. Indexed on `AssessmentDate`.

| Field | Notes |
| ----- | ----- |
| `FloodRiskAssessmentId` | PK |
| `CanalId` | FK |
| `RiskScore` | int |
| `RiskLevel` | Low / Medium / High / Critical |
| `AssessmentDetails` | Explanation text |
| `ModelVersion` | `Rule-Based v1.0` |
| `AssessmentDate`, `ValidUntil` | Validity window |

### 3.8 Announcement — `Models/Announcement.cs` → `Announcements`

| Field | Notes |
| ----- | ----- |
| `AnnouncementId` | PK |
| `Title*`, `Content` | Body |
| `ImagePath` | e.g. `/uploads/announcements/abc.jpg` |
| `TargetAudience` | All / LGU / Barangay / Resident etc. |
| `Location` | Optional |
| `PostedByUserId` | FK → AspNetUsers |
| `CreatedAt` | Timestamp |

### 3.9 Notification — `Models/Notification.cs` → `Notifications`

Per-user inbox, pushed via SignalR.

| Field | Notes |
| ----- | ----- |
| `NotificationId` | PK |
| `UserId` | FK → AspNetUsers |
| `Title`, `Message`, `Type` | Content |
| `RelatedAlertId?`, `RelatedReportId?` | Nullable links |
| `IsRead` | Defaults to `false` |
| `CreatedAt` | Timestamp |

### 3.10 SupportTicket & SupportMessage — `Models/SupportTicket.cs`, `Models/SupportMessage.cs`

Resident help-center tickets and their chat messages.

| SupportTicket field | Notes |
| ------------------- | ----- |
| `SupportTicketId` | PK |
| `UserId` | FK → AspNetUsers (resident) |
| `Subject*`, `Description*` | Capped at 150 / 2000 chars |
| `Category`, `Priority`, `Status` | Lookup-backed values; statuses drive the staff queue |
| `AssignedToUserId?` | Staff assignee (SetNull on delete) |
| `CreatedAt`, `UpdatedAt`, `ClosedAt?` | Timestamps |

| SupportMessage field | Notes |
| -------------------- | ----- |
| `SupportMessageId` | PK |
| `SupportTicketId` | FK → SupportTickets (cascade) |
| `SenderUserId` | FK → AspNetUsers (restrict) |
| `Message*` | Capped at 2000 chars |
| `IsInternal`, `IsRead`, `SentAt` | Internal staff notes, read tracking, ordering |

### 3.11 PendingRegistration — `Models/PendingRegistration.cs`

Temporary registration record created before email OTP verification. No `ApplicationUser` row exists until the code is verified. Unique normalized email/username indexes, expiry, attempt/send counters, hashed OTP.

### 3.12 LookupOption — `Models/LookupOption.cs`

Editable dropdown values (report types, ticket categories/priorities/statuses, announcement audiences, staff roles). Unique `(Category, Value)`; `IsActive` / `SortOrder` / `DisplayName`; seeded idempotently without overwriting operator changes.

### 3.13 TrustedDevice — `Models/TrustedDevice.cs`

One row per browser/device that may skip the TOTP prompt for `TwoFactor:TrustedDeviceDays` (default 30, hard cap 30). Stores a SHA-256 **hash** of the cookie token, device label, user agent, created/last-seen/expiry timestamps. Deleting a user cascades their devices; expired rows are purged by a hosted service.

### 3.14 ViewModels (not tables)

- `Models/AccountViewModels.cs`:
  - `LoginViewModel`: `Username`, `Password`, `RememberMe`
  - `RegisterViewModel`: `FullName*`, `UserName*`, `Email*`, `PhoneNumber*`, `Password* (min 6)`, `ConfirmPassword*`, `Barangay?`, `City?`, `Address?`, `Role = Resident`
  - `ForgotPasswordViewModel`: `Email*` (email format validated)
  - `AccountResetPasswordViewModel`: `Email*`, `Token*` (Base64Url Identity token from the email link), `NewPassword* (min 6)`, `ConfirmPassword*` (must match `NewPassword`)
  - `ProfileViewModel`: `UserName` (display only, immutable), `FullName*`, `Email*`, `PhoneNumber?`, `Barangay?`, `City?`, `Address?`, `CurrentPassword?` + `NewPassword?` (optional password change)
  - `DeleteAccountViewModel`: `UserName`, `Email` (display), `Password*` (confirmation)
  - `VerifyOtpViewModel`: `Email`, `Code` (exactly 6 digits)
  - `TwoFactorLoginViewModel`: `ChallengeId`, `Code`, `RememberMe`, `RememberDevice`, `DeviceName?`
  - `EnableTwoFactorViewModel`: `Code` (6-digit authenticator code) + QR/manual key display data
  - `TwoFactorRecoveryCodesViewModel`, `TrustedDevicesViewModel` (devices, 2FA state, recovery-code count)
- `Models/DashboardViewModel.cs`: aggregated counts (`TotalCanals`, `TotalSensors`, `ActiveAlerts`, `PendingReports`, `HighRiskCanals`, `RegisteredUsers`, `Online/OfflineSensors`) + lists (`RecentAlerts`, `RecentReports`, `RecentRiskAssessments`, `CanalStatusOverview`, `SensorStatus`, `LatestReadings`, `Announcements`) + `CurrentRole`, `UserName`, `UserBarangay`, `UnreadNotifications`. Built by `DashboardController.BuildDashboardViewModelAsync`.

---

## 4. Controllers — `Controllers/`

### 4.1 AccountController — `Controllers/AccountController.cs`

Base: `/Account`

| URL | View File | Access |
| --- | --------- | ------ |
| `GET + POST /Account/Login` | `Views/Account/Login.cshtml` | Anonymous |
| `GET + POST /Account/Register` | `Views/Account/Register.cshtml` | Anonymous |
| `GET + POST /Account/VerifyEmail?email=...` (6-digit OTP) | `Views/Account/VerifyEmail.cshtml` | Anonymous |
| `GET + POST /Account/ForgotPassword` | `Views/Account/ForgotPassword.cshtml` | Anonymous |
| `GET /Account/ForgotPasswordConfirmation` | `Views/Account/ForgotPasswordConfirmation.cshtml` | Anonymous |
| `GET + POST /Account/ResetPassword?email=...&token=...` | `Views/Account/ResetPassword.cshtml` | Anonymous |
| `GET /Account/ResetPasswordConfirmation` | `Views/Account/ResetPasswordConfirmation.cshtml` | Anonymous |
| `GET /Account/Profile` (update own account) | `Views/Account/Profile.cshtml` | Logged in |
| `GET + POST /Account/EnableTwoFactor` (TOTP setup + QR) | `Views/Account/EnableTwoFactor.cshtml` | Logged in |
| `GET + POST /Account/TwoFactor` (login challenge) | `Views/Account/TwoFactor.cshtml` | Anonymous (valid challenge) |
| `GET /Account/TrustedDevices` | `Views/Account/TrustedDevices.cshtml` | Logged in |
| `POST /Account/DisableTwoFactor`, `/ResetAuthenticator`, `/GenerateRecoveryCodes`, `/RevokeTrustedDevice`, `/RevokeAllTrustedDevices` | `TwoFactorRecoveryCodes.cshtml`, `TrustedDevices.cshtml` | Logged in (own account, antiforgery) |
| `GET + POST /Account/Delete` (delete own account) | `Views/Account/Delete.cshtml` | Logged in |
| `GET /Account/ConfirmEmailChange` | — (confirmation redirect) | Logged in |
| `POST /Account/Logout` | — (redirect to Login) | Logged in |

Notes: login accepts username **or** email (+ Remember Me checkbox → 7-day persistent cookie, sliding expiration); unverified accounts cannot sign in until they complete email-OTP verification (`PendingRegistrations` table, 10-min code, 60s resend cooldown, 5 attempts, 5 sends/hour, 24h pending expiry — no `ApplicationUser` row exists before verification); register role is allow-listed to non-staff roles (crafted `Role=Admin` POSTs fall back to Resident; staff accounts come from seeder or Admin → Users); unknown roles fall back to Resident; failed role assignment rolls back the user. Forgot Password is email-based via the Brevo HTTPS API — unknown emails show an explicit not-found error — full step-by-step in §11. Register and Reset enforce the Identity password policy (min 6, at least 1 uppercase, 1 number, 1 special character; lowercase is optional) with hints under each field. `Profile` edits FullName/Email/Phone/Barangay/City/Address plus optional password change (current + new); changing email or password updates the security stamp and revokes trusted devices. `Delete` requires password confirmation and blocks deleting the last remaining Admin. Top navbar Profile links to `/Account/Profile`; Login page links to Forgot Password.

**Two-factor authentication.** After a password check, a user with 2FA enabled does **not** receive the auth cookie yet. The app issues an opaque 32-character, single-use challenge stored in `IMemoryCache` for 10 minutes; the challenge survives up to 5 code attempts. `/Account/TwoFactor` accepts either the current 6-digit TOTP code or a single-use recovery code. A successful login can remember the browser as a trusted device for up to 30 days (token stored as a SHA-256 hash in `TrustedDevices`; cookie flags are set by `TrustedDeviceCookie`). Disabling 2FA, resetting the authenticator, changing the password/email, or resetting a password revokes all trusted devices. All auth POSTs are rate-limited (10/min per IP + submitted username → HTTP 429) and antiforgery-protected.

### 4.2 DashboardController — `Controllers/DashboardController.cs`

Base: `/Dashboard` — default app route (`/` → `/Dashboard/Index` → role redirect).

| URL | View File | Access |
| --- | --------- | ------ |
| `GET /Dashboard/Index` | — (redirects by role) | Any logged-in user |
| `GET /Dashboard/Admin` | `Views/Dashboard/Admin.cshtml` | Admin |
| `GET /Dashboard/Lgu` | `Views/Dashboard/Lgu.cshtml` | LGU |
| `GET /Dashboard/Barangay` | `Views/Dashboard/Barangay.cshtml` | Barangay |
| `GET /Dashboard/Maintenance` | `Views/Dashboard/Maintenance.cshtml` | Maintenance |
| `GET /Dashboard/Resident` | `Views/Dashboard/Resident.cshtml` | Resident |
| `GET /Dashboard/AccessDenied` | `Views/Dashboard/AccessDenied.cshtml` | Public |
| `GET /Dashboard/HttpError` | `Views/Dashboard/HttpError.cshtml` | Public |
| `GET /Dashboard/Error` | `Views/Dashboard/Error.cshtml` | Public |

### 4.3 CanalsController — `Controllers/CanalsController.cs`

Base: `/Canals` — Views in `Views/Canals/`.

| Action | URL Example | Access |
| ------ | ----------- | ------ |
| Index | `/Canals` | Admin, LGU, Barangay, Maintenance (Residents excluded) |
| Details | `/Canals/Details/5` | Admin, LGU, Barangay, Maintenance |
| Map | `/Canals/Map` | Admin, LGU, Barangay, Maintenance |
| Create | `/Canals/Create` | Admin only |
| Edit | `/Canals/Edit/5` | Admin only |
| Delete | `/Canals/Delete/5` | Admin only |

Map uses Leaflet + OpenStreetMap, locked to Cebu bounds (`Views/Canals/Map.cshtml`). Create/Edit use a Cebu-only drag-and-pin picker (`Views/Canals/Create.cshtml`, `Edit.cshtml`) centered on Cebu City (10.3157, 123.8854).

### 4.4 SensorsController — `Controllers/SensorsController.cs`

Base: `/Sensors` — Views in `Views/Sensors/`.

| Action | URL Example | Access |
| ------ | ----------- | ------ |
| Index | `/Sensors` | Staff roles |
| Details | `/Sensors/Details/5` | Staff roles |
| Create | `/Sensors/Create` | Admin only |
| Edit | `/Sensors/Edit/5` | Admin only |
| Delete | `/Sensors/Delete/5` | Admin only |

### 4.5 SensorReadingsController — `Controllers/SensorReadingsController.cs`

Base: `/SensorReadings` — Views in `Views/SensorReadings/`.

| URL | Access |
| --- | ------ |
| `GET /SensorReadings` (filter by canal / date) | Admin, LGU, Barangay, Maintenance |
| `GET /SensorReadings/Details/5` | Admin, LGU, Barangay, Maintenance |
| `GET /SensorReadings/ChartData?canalId=5&hours=5&maxPoints=300` | Admin, LGU, Barangay, Maintenance |
| `GET /SensorReadings/WaterLevelTrendJson?hours=5&maxPoints=300` | Admin, LGU, Barangay, Maintenance |
| `GET /SensorReadings/LatestReadingsJson` | Admin, LGU, Barangay, Maintenance, Resident |
| `POST /SensorReadings/GenerateTestReading` | Admin, LGU, Barangay, Maintenance |

No manual create — rows come from simulation or the service layer. Staff roles use the full readings table; Residents receive the read-only latest-reading feed used by their dashboard. Barangay users are scoped server-side to canals in their own barangay.

- `ChartData` returns one canal's real readings (optionally a custom PH-time range), sorted chronologically and downsampled with `ChartDataDownsampler` (20–2000 points, actual min/max preserved).
- `WaterLevelTrendJson` returns one time series per visible canal for the last 1–720 hours (default 5h). It reads only stored readings, drops non-finite values, sorts by time, omits canals with no readings, and never pads/fabricates points. This is what `wwwroot/js/water-trend.js` renders on the Admin and Barangay dashboards.
- `LatestReadingsJson` remains the single-latest-reading feed used elsewhere (e.g. Resident/status cards); it is not a trend source.

### 4.6 AlertsController — `Controllers/AlertsController.cs`

Base: `/Alerts` — Views in `Views/Alerts/`.

| URL | Access |
| --- | ------ |
| `GET /Alerts` | Admin, LGU, Barangay, Maintenance |
| `GET /Alerts/Details/5` | Admin, LGU, Barangay, Maintenance |
| Acknowledge / Assign / Resolve (POST) | Admin, Maintenance |
| Delete / Close | Admin only |

### 4.7 FloodRiskController — `Controllers/FloodRiskController.cs`

Base: `/FloodRisk` — Views in `Views/FloodRisk/`.

| URL | Access |
| --- | ------ |
| `GET /FloodRisk` (latest per canal) | Admin, LGU, Barangay, Maintenance |
| `GET /FloodRisk/Details/5` | Admin, LGU, Barangay, Maintenance |
| Recalculate / Assess (POST) | Admin, LGU |

### 4.8 ReportsController (Community Reports) — `Controllers/ReportsController.cs`

Base: `/Reports` — Views in `Views/Reports/`.

| URL | View File | Access |
| --- | --------- | ------ |
| `GET + POST /Reports/Create` | `Views/Reports/Create.cshtml` (Cebu-only map pin + photo) | Resident only |
| `GET /Reports/MyReports` | `Views/Reports/MyReports.cshtml` | Resident only |
| `GET /Reports` | `Views/Reports/Index.cshtml` | Admin, LGU, Barangay, Maintenance |
| `GET /Reports/Details/5` | `Views/Reports/Details.cshtml` | Admin, LGU, Barangay, Maintenance |
| Review / Status update (POST) | — | Admin, LGU, Barangay |
| Delete | `Views/Reports/Delete.cshtml` | Admin only |

Photo upload saved to `wwwroot/uploads/reports/`.

### 4.9 AnnouncementsController — `Controllers/AnnouncementsController.cs`

Base: `/Announcements` — Views in `Views/Announcements/`.

| Action | Access |
| ------ | ------ |
| Index, Details | All logged-in users |
| Create, Edit, Delete | Admin, LGU only |

Image upload saved to `wwwroot/uploads/announcements/`.

### 4.10 NotificationsController — `Controllers/NotificationsController.cs`

Base: `/Notifications`.

| URL | View File | Access |
| --- | --------- | ------ |
| `GET /Notifications` (my inbox) | `Views/Notifications/Index.cshtml` | Logged in |
| `GET /Notifications/All` | `Views/Notifications/All.cshtml` | Admin only |

Bell count: `INotificationService.GetUnreadCountAsync` + SignalR.

### 4.11 UsersController — `Controllers/UsersController.cs`

Base: `/Users` — class-level `[Authorize(Roles = "Admin")]`.

| URL | View File |
| --- | --------- |
| `GET /Users` | `Views/Users/Index.cshtml` |
| `GET /Users/Details/5` | `Views/Users/Details.cshtml` |
| `GET + POST /Users/Edit/5` | `Views/Users/Edit.cshtml` |
| `GET + POST /Users/Delete/5` | `Views/Users/Delete.cshtml` |
| `GET + POST /Users/ResetPassword/5` | `Views/Users/ResetPassword.cshtml` |

### 4.12 FloodNewsController — `Controllers/FloodNewsController.cs`

Base: `/FloodNews` — class-level `[Authorize]` (all logged-in roles).

| URL | View File | Access |
| --- | --------- | ------ |
| `GET /FloodNews` (optional `?refresh=true&source=...`) | `Views/FloodNews/Index.cshtml` | Logged in |
| `GET /FloodNews/Panel` (async dashboard fragment) | `Views/Shared/_FloodNewsPanel.cshtml` | Logged in |

Aggregates public **keyless** RSS feeds: GDACS disaster alerts (Philippines only, flood/tropical-cyclone events) plus Philippine news feeds, filtered by `FloodRelevance`. Successful results are cached for `FloodNews:CacheMinutes` (20 default); a manual refresh is limited to once per 2 minutes; on provider failure the last good result is served marked stale, otherwise an unavailable state is shown. Informational only — it never feeds flood-risk scores.

### 4.13 CustomerServiceController — `Controllers/CustomerServiceController.cs`

Base: `/CustomerService` — `[Authorize]` with action-level roles.

| Area | URLs | Access |
| ---- | ---- | ------ |
| Resident help center | `/CustomerService`, `/MyTickets`, `/Create`, `/Ticket/5`, `/Chat` | Resident |
| Staff workspace | `/CustomerService/Dashboard`, `/Tickets`, `/Manage/5`, `/UnreadCount` | CustomerService, Admin |
| Staff actions | Reply, UpdateStatus, AssignToMe, AddNote, CloseTicketStaff | CustomerService, Admin |
| Delete ticket | `POST /CustomerService/DeleteTicket` | Admin only |

Messages are rate-limited per user (5s cooldown / 30 per hour; tickets 30s / 10 per hour) and pushed through `SupportHub` at `/supporthub`.

### 4.14 HomeController — `Controllers/HomeController.cs`

Public pages (not the app default):

| URL | View File |
| --- | --------- |
| `GET /Home/Index` | `Views/Home/Index.cshtml` |
| `GET /Home/Privacy` | `Views/Home/Privacy.cshtml` |

SignalR hubs: `Hubs/MonitoringHub.cs` at `/monitoringhub` and `Hubs/SupportHub.cs` at `/supporthub` (both `[Authorize]`; monitoring groups `user_{id}`, `role_{role}`, `canal_{id}`). A `/health` liveness endpoint returns JSON without touching the database.

---

## 5. UI / View Locations — Where to Click in the Browser

Views live in `Views/<Controller>/<Action>.cshtml`.

Layout: `Views/Shared/_Layout.cshtml` + `_Sidebar.cshtml` + `_TopNavbar.cshtml`.

### Admin

Landing: `/Dashboard/Admin` (`Views/Dashboard/Admin.cshtml`)

Demo these:

- `/Canals` + `/Canals/Map` (CRUD)
- `/Sensors` + `/SensorReadings`
- `/Alerts` (assign / resolve)
- `/FloodRisk` (recalculate)
- `/Reports` (triage all reports)
- `/Announcements` (Create / Edit / Delete)
- `/Users` (edit roles, reset password)
- `/Notifications/All`
- `/FloodNews` (read-only PH flood news)

### LGU

Landing: `/Dashboard/Lgu` (`Views/Dashboard/Lgu.cshtml`)

Demo these:

- `/Dashboard/Lgu` (city overview cards)
- `/FloodRisk` (city risk)
- `/Alerts`
- `/Reports` (review)
- `/Announcements` (post city-wide)
- `/Canals/Map`
- `/FloodNews`

### Barangay

Landing: `/Dashboard/Barangay` (`Views/Dashboard/Barangay.cshtml`)

Demo these:

- `/Dashboard/Barangay` (barangay canals / sensors)
- `/Reports` (own-barangay reports)
- `/Alerts`
- `/Announcements` (read)
- `/SensorReadings`
- `/FloodNews`

### Maintenance

Landing: `/Dashboard/Maintenance` (`Views/Dashboard/Maintenance.cshtml`)

Demo these:

- `/Alerts` (Acknowledge → In Progress → Resolved)
- `/Sensors` (status)
- `/SensorReadings`
- `/FloodRisk`
- `/Reports` (field verification)
- `/FloodNews`

### Resident

Landing: `/Dashboard/Resident` (`Views/Dashboard/Resident.cshtml`)

The Canal Water Levels panel loads the read-only `LatestReadingsJson` feed on page load and polls every 30 seconds. It shows the canal name, latest water level in metres, and the reading timestamp; it has explicit loading, empty, and error/retry states. SignalR live updates are not used for Residents.

Demo these (no Canals tab — removed for Residents):

- `/Reports/Create` (submit + photo + Cebu map pin)
- `/Reports/MyReports` (track Pending → Under Review → Resolved)
- `/Announcements` (read)
- `/Notifications` (my inbox)
- `/CustomerService` (help center + support tickets/chat)
- `/FloodNews`

### Customer Service

Landing: `/CustomerService/Dashboard`

Demo these:

- `/CustomerService/Dashboard` (queue metrics)
- `/CustomerService/Tickets` (filter by status/priority)
- `/CustomerService/Manage/5` (reply, assign, internal notes, close)
- `/FloodNews`

### All users

- `/Account/Login` — accepts username **or** email
- `/Account/Register` — creates Resident only (staff accounts come from seeder or Admin → Users)
- `/Account/Profile` — edit details, change password, open 2FA settings
- `/Account/EnableTwoFactor` → `/Account/TrustedDevices` — TOTP setup, recovery codes, device revocation

> Walkthrough tip: start at `/Account/Login` → Admin → `/Dashboard/Admin` cards (0 on fresh DB) → `Canals/Create` → `Sensors/Create` → Resident `Reports/Create` → Maintenance `Alerts` → `FloodRisk` → LGU `Announcements/Create` → bell notification → `/FloodNews` → `/Account/EnableTwoFactor`.

---

## 6. Seeded Accounts — Where They Are Stored

**There is no password file. Accounts are seeded into PostgreSQL on startup.**

| Question | Answer |
| -------- | ------ |
| Seed code file | `Services/DataSeeder.cs` — `EnsureRolesAsync()` creates 6 roles; `SeedUsersAsync()` creates 4 staff demo users (no Resident demo) |
| When does it run? | Every startup in `Program.cs` — `db.Database.Migrate()` then `await seeder.SeedAsync()` |
| Where in DB? | PostgreSQL (see connection string), tables `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles` |
| Demo data? | No demo canals / sensors — dashboards start at 0 |
| Password storage | Hashed by `UserManager.CreateAsync`; the seed file resets the four demo accounts to their seed-time development passwords on every startup |
| Credentials in this README? | Deliberately **not** reproduced. Read the four dev-only credentials from `Services/DataSeeder.cs`, or set your own after seeding via Admin → Users / Profile. Rotate or remove demo accounts before any real deployment. |
| Lockout | 5 failed attempts → 5-min lock (`Program.cs`, enforced in `AccountController`) |

### Seeded roles and users

| Role | Account seeded? | Notes |
| ---- | --------------- | ----- |
| Admin | Yes | Full access, user management, destructive actions |
| LGU | Yes | City oversight, announcements, risk recalculation |
| Barangay | Yes | Own-barangay scope, reports, readings |
| Maintenance | Yes | Alert triage, field verification |
| Resident | No | Self-registers via `/Account/Register` (OTP-verified) |
| CustomerService | No | Created by an Admin via `/Users`; gets the support workspace |

No Resident demo account is seeded. Residents self-register with role `Resident`; manage them at `/Users` (Admin only). If a `resident@coms.gov` row exists in an older database, it is left untouched by seeding — see `Services/DataSeeder.cs` for safe-identification guidance. Never commit real credentials; the seeded passwords exist only for local development/hosted demos and should be rotated.

---

## 7. Services, Uploads, and Config Files

| Item | File | Notes |
| ---- | ---- | ----- |
| DataSeeder | `Services/DataSeeder.cs` | Seeds 6 roles + 4 staff users + lookup options only |
| Email (Brevo) | `Services/EmailService.cs` (`IEmailService`, `BrevoEmailService`, `BrevoOptions`) | Transactional email over HTTPS for OTP + password resets (see §11); plain `HttpClient`, no SDK package |
| SensorService | `Services/SensorService.cs` | Sensor helpers + simulated readings |
| AlertService | `Services/AlertService.cs` | Threshold alerts with per-type 30-minute dedup + per-canal serializable lock; staff notifications after commit |
| Alert retention | `Services/AlertRetentionService.cs` | Hosted sweep; default 5h window shared by all alert read paths |
| FloodRiskService | `Services/FloodRiskService.cs` | Rule-based score → `FloodRiskAssessments` |
| Flood-risk retention | `Services/FloodRiskRetentionService.cs` | Prunes assessment history while keeping the latest per canal |
| NotificationService | `Services/NotificationService.cs` | Inbox + unread count + SignalR dispatch |
| SensorSimulationService | `Services/SensorSimulationService.cs` | Background readings every 20s; disabled by default |
| ChartDataDownsampler | `Services/ChartDataDownsampler.cs` | Bounded chart sampling that keeps real peak/trough points |
| Flood News | `Services/FloodNewsService.cs`, `FloodRelevance.cs` | Keyless GDACS + PH RSS, strict flood/PH filtering, cache + stale fallback |
| Image upload gate | `Services/ImageUploadValidator.cs` | JPEG/PNG only: 5MB, extension + MIME + magic bytes + real SkiaSharp decode + 16px minimum + 4096px cap |
| Image authenticity | `Services/ImageVerificationService.cs` | Advisory post-save heuristic (generator/editor metadata via EXIF `Software` tag, format mismatch, stripped metadata); never blocks the report |
| Image relevance | `Services/ImageRelevanceService.cs` | Synchronous offline pixel heuristic; Irrelevant/Uncertain photos are rejected before save |
| Text moderation | `Services/TextModerationService.cs` | Report/ticket text checks |
| Trusted devices | `Services/TrustedDeviceService.cs`, `TrustedDeviceCleanupService.cs` | Hashed tokens, 30-day max trust, periodic expiry purge |
| Cebu boundary | `Services/CebuBoundaryService.cs` | Ray-casting against `wwwroot/data/cebu-report-area.geojson` |
| Lookup options | `Services/LookupService.cs` | Editable dropdowns, idempotent seed |
| Report uploads | `Controllers/ReportsController.cs` | Validated in memory → `wwwroot/uploads/reports/` |
| Announcement uploads | `Controllers/AnnouncementsController.cs` | Validated → `wwwroot/uploads/announcements/` |
| DB context | `Data/ApplicationDbContext.cs` | 13 domain DbSets + Identity, unique `SensorCode`, composite indexes, Restrict / SetNull deletes |
| Startup | `Program.cs` | DI, Identity/2FA, cookies, SignalR, antiforgery, rate limiting, forwarded headers, compression, migrate + seed, friendly 404, `/health` |

Upload folders are auto-created at runtime (`Directory.CreateDirectory`). DB stores the `/uploads/...` path (`PhotoPath` / `ImagePath`). Rejected uploads are validated entirely in memory and never touch disk.

**Image pipeline / ImageSharp removal:** the app uses **SkiaSharp 2.88.2** (plus `SkiaSharp.NativeAssets.Linux` for containers) for image decoding, EXIF reading, and pixel analysis. There are no Six Labors / ImageSharp package or namespace references in the web or test projects. SkiaSharp 2.88.2 has a published NU1903 advisory; upgrades should be validated against the decode/pixel-cap tests before shipping.

`appsettings.json` (non-secret defaults; replace values through user-secrets or environment variables):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=comsdb;Username=comsuser;Password=YOUR_PASSWORD_HERE"
  },
  "MapSettings": { "UseOpenStreetMap": true, "MapboxToken": "", "GoogleMapsApiKey": "" },
  "DemoSettings": { "EnableSimulation": false, "SimulationIntervalSeconds": 20 },
  "AlertRetention": { "RetentionHours": 5, "CleanupIntervalMinutes": 5 },
  "TwoFactor": { "TrustedDeviceDays": 30, "CleanupIntervalMinutes": 60 },
  "FloodRiskRetention": { "RetentionHours": 5, "CleanupIntervalMinutes": 30, "HistoryPageSize": 20, "CleanupBatchSize": 2000 },
  "Email": { "ApiKey": "", "SenderEmail": "", "SenderName": "COMS" },
  "ImageVerification": { "Enabled": true, "Provider": "Heuristic", "ApiKey": "" },
  "ImageRelevance": { "Enabled": true },
  "TextModeration": { "Enabled": true },
  "FloodNews": { "Enabled": true, "CacheMinutes": 20, "MaxArticles": 30, "MaxAgeDays": 7, "HttpTimeoutSeconds": 10 }
}
```

`Email` feeds `BrevoOptions` (and `Brevo:*` takes precedence) for OTP + password-reset delivery (see §11). The real API key is **never committed** — store it in user-secrets (`dotnet user-secrets set "Email:ApiKey" "..."`) or `Email__ApiKey` / `Brevo__ApiKey` environment variables. Brevo keys start with `xkeysib-`; a leftover Resend `re_...` key is rejected with HTTP 401 and logged at startup. `FloodNews` needs no API key — both providers are public RSS feeds.

---

## 8. Database — How It Is Connected (PostgreSQL)

### 8.1 What is used

| Item | Value |
| ---- | ----- |
| Database engine | **PostgreSQL 17+** (server, port `5432`) |
| Database name | **`comsdb`** |
| Login role | **`comsuser`** (owner of `comsdb` and schema `public`) |
| EF Core provider | **`Npgsql.EntityFrameworkCore.PostgreSQL` 9.0.x** (see `COMS MVC.csproj`) |
| ORM | Entity Framework Core 9 |
| Context | `Data/ApplicationDbContext.cs` (Identity + 13 domain `DbSet`s) |
| Migrations | `Migrations/` — 11 migrations (see list below) |

### 8.2 Migrations

1. `InitialCreate`
2. `AddValidationConstraints`
3. `AddReportCoordinates`
4. `AddEmailOtpFields`
5. `AddPendingRegistrations`
6. `MakeReportCanalOptional`
7. `AddImageVerificationFields`
8. `AddCustomerService`
9. `AddLookupOptionsAndFieldLimits`
10. `AddImageRelevanceFields`
11. `AddTwoFactorTrustedDevicesAndPerformanceIndexes`

### 8.3 Connection string (`appsettings.json`)

```json
"ConnectionStrings": {
  "DefaultConnection": "Host=localhost;Port=5432;Database=comsdb;Username=comsuser;Password=YOUR_PASSWORD_HERE"
}
```

Parts: `Host` = DB server (use the PC's LAN IP for a second machine), `Port` = `5432` (PostgreSQL default), `Database` = `comsdb`, `Username`/`Password` = `comsuser` credentials.

### 8.4 How the app connects (`Program.cs`)

```csharp
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=coms.db";
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
```

On every startup the app runs `db.Database.Migrate()` (auto-applies pending migrations) then `IDataSeeder.SeedAsync()` (creates roles, demo users, lookup options — see §6). So after `dotnet ef database update`, just `dotnet run` is enough; the schema stays current automatically.

### 8.5 Tables created by `ApplicationDbContext`

Identity: `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoleClaims`. Domain: `Canals`, `Sensors`, `SensorReadings`, `ObstructionAlerts`, `CommunityReports`, `SupportTickets`, `SupportMessages`, `FloodRiskAssessments`, `Notifications`, `Announcements`, `PendingRegistrations`, `LookupOptions`, `TrustedDevices`, plus `__EFMigrationsHistory`.

Key constraints (Fluent API in `ApplicationDbContext.cs`): unique `Sensors.SensorCode`, unique normalized email/username on `PendingRegistrations`, unique `(Category, Value)` on `LookupOptions`, indexes on `RecordedAt` / `(CanalId, RecordedAt)` / `DetectedAt` / `(CanalId, Status)` / `CreatedAt` / `AssessmentDate` / `(UserId, IsRead)` / `(UserId, ExpiresAtUtc)`, `DeleteBehavior.Restrict` on parent links (deleting a canal/sensor with children fails with a friendly message instead of cascading), `SetNull` for optional alert/ticket links, and `Cascade` for trusted devices + support messages.

### 8.6 Setup from scratch (for panel / reproduction)

Prerequisites: .NET SDK 10.0 + PostgreSQL 17+.

1. Create database `comsdb` owned by login role `comsuser`.
2. On `comsdb` run:

```sql
GRANT ALL ON SCHEMA public TO comsuser;
ALTER SCHEMA public OWNER TO comsuser;
```

3. Set the connection string in `appsettings.json`.
4. Apply migrations:

```bash
dotnet ef database update
```

5. Verify in pgAdmin/psql: tables listed under `comsdb > Schemas > public > Tables`, and `__EFMigrationsHistory` shows 11 applied migrations.

### Run

```bash
dotnet run
```

Open `http://localhost:5070` → redirected to `/Account/Login` (or `/Dashboard` if remembered). Log in with any account from §6. Fresh install shows 0 canals / sensors / alerts until Admin adds them via `/Canals/Create` → `/Sensors/Create`.

### Migrations (after model changes)

```bash
dotnet ef migrations add <Name>
dotnet ef database update
```

### Remote DB access (second machine, same LAN)

1. `postgresql.conf`: `listen_addresses = '*'`
2. `pg_hba.conf`: `host all all 192.168.1.0/24 scram-sha-256`
3. Reload: `SELECT pg_reload_conf();`
4. Windows Firewall: allow TCP 5432 scoped to LocalSubnet
5. Point connection string at `Host=<this-PC-IP>`

---

## 9. Suggested Defense Flow (5 minutes)

1. **Login** (`/Account/Login`) — validation, lockout message, role redirect, optional TOTP challenge (`/Account/TwoFactor`).
2. **Admin dashboard** (`/Dashboard/Admin`) — `DashboardViewModel` counts + `BuildDashboardViewModelAsync` + the real-reading Water Level Trend chart.
3. **Canals + Map** (`/Canals`, `/Canals/Map`) — Cebu-locked drag-and-pin picker on Create; `Models/Canal.cs` thresholds + Leaflet view.
4. **Sensors + Readings** (`/Sensors`, `/SensorReadings`) — same Cebu picker on sensor Create (pin follows canal); `ChartData` / `WaterLevelTrendJson` endpoints back the chart.
5. **Resident report** (Resident → `/Reports/Create` with validated photo + Cebu map pin → `wwwroot/uploads/reports/`) — relevance gate before save, advisory authenticity analysis after save.
6. **Alert triage** (Maintenance → `/Alerts` acknowledge / resolve) — `AlertService.cs` threshold rules + 30-minute per-type dedup.
7. **Flood risk** (`/FloodRisk`) — `FloodRiskService.cs` + `Models/FloodRiskAssessment.cs`.
8. **Announcement + Notification** (`/Announcements/Create` as LGU → bell via `/monitoringhub`) — `MonitoringHub.cs` + `NotificationService.cs`.
9. **Flood News** (`/FloodNews`) — keyless GDACS + PH RSS, cache/stale/error states, never affects risk scores.
10. **Support** (Resident `/CustomerService` ↔ CustomerService/Admin `/CustomerService/Tickets`) — `SupportHub` real-time chat.
11. **Users + Seeder** (`/Users` + `Services/DataSeeder.cs`) — "where are temporary accounts stored?" → DB tables + seed file; credentials intentionally not reproduced here.

---

## 10. Latest Features & Recent Updates

### Two-factor authentication (TOTP) + trusted devices

- `/Account/EnableTwoFactor` generates an authenticator key and QR code (QRCoder), verifies the first 6-digit code, enables 2FA, and shows 10 single-use recovery codes once.
- `/Account/TwoFactor` is the login challenge: the auth cookie is withheld until a valid TOTP or recovery code is entered. Challenges are opaque/single-use, expire in 10 minutes, and lock after 5 bad codes.
- "Remember this device" issues a random token stored **hashed** (SHA-256) in `TrustedDevices` for at most 30 days (`TwoFactor:TrustedDeviceDays`). `/Account/TrustedDevices` lists active devices and supports revoking one or all.
- Disabling 2FA, resetting the authenticator, changing a password/email, or completing a password reset revokes trusted devices and refreshes the security stamp. Expired device rows are purged periodically.
- All auth POSTs share the `auth` rate limiter (10/minute per IP + username → HTTP 429), antiforgery validation, and Identity lockout (5 attempts / 5 minutes).

### Notification bell badge alignment

- Root cause: Bootstrap's `.badge` is inline-block with `line-height: 1`, and the old markup mixed a fixed 20px height with `start-100` / `translate-middle`; the count was not vertically centered and shifted by digit count.
- Fix: the bell is wrapped in an inline-flex `.notification-bell-icon`; `#notification-count-badge` is a fixed 20×20 circle, `position: absolute`, `display: inline-flex`, centered on both axes, zero padding, `border-radius: 50%`. The same geometry works for 1-, 2-, and 3-digit counts at every breakpoint without pixel hardcoding per device.
- The unread count and SignalR refresh behavior are unchanged.

### Water Level Trend chart (dashboard fix)

- Root cause: the Admin and Barangay dashboard charts called `LatestReadingsJson`, which returns only the single newest reading per canal. A "trend" therefore had at most one point per canal (and Barangay used canal names as x-axis labels).
- Fix: `GET /SensorReadings/WaterLevelTrendJson` returns one real time series per visible canal for the last 5 hours (configurable 1–720h), scoped server-side for Barangay users, sorted chronologically, with non-finite values dropped and `ChartDataDownsampler` used to keep series bounded while preserving actual peaks/troughs.
- Shared partial `Views/Shared/_WaterLevelTrendChart.cshtml` + `wwwroot/js/water-trend.js` render a multi-series Chart.js line chart (time on the x-axis, water level in metres, Philippines UTC+8 labels). States are explicit:
  - **No readings** → "No water-level readings yet" empty state.
  - **One point / only one point per canal** → the real point is plotted with a note that two readings are needed for a trend line.
  - **Provider/API failure** → warning state, never a fabricated chart.
- Refreshes every 30 seconds while the tab is visible, respects `prefers-reduced-motion` via `animation: false`, and re-renders colors when the light/dark theme attribute changes. No random, sample, or padded values exist anywhere in the endpoint or script.

### Alert deduplication + retention

- Each alert type (water-level warning/critical, obstruction, severe obstruction) is independent, so one reading may legitimately raise several types.
- Repeats **of the same type** are suppressed while an Active/Acknowledged/In Progress alert of that type exists within the last 30 minutes; the existing alert is refreshed with the latest reading instead of duplicating history. A resolved condition that recurs creates a new alert.
- PostgreSQL row locking (`FOR UPDATE`) inside a serializable transaction prevents concurrent readings for the same canal from inserting duplicate alerts. SQLite test runs skip the provider-specific lock.
- Hosted `AlertRetentionService` removes alerts older than `AlertRetention:RetentionHours` (default 5h) on a fixed interval, and all read paths apply the same window. `FloodRiskRetentionService` prunes old assessment history while retaining the latest per canal.

### Image upload, relevance, authenticity, and tests

- Every report/announcement image passes one shared gate (`ImageUploadValidator`): 5MB limit, `.jpg` / `.jpeg` / `.png` extension, allow-listed MIME type, JPEG/PNG magic bytes, real SkiaSharp decode, minimum 16×16, maximum 4096×4096 (decompression-bomb guard). Validation happens in memory before any disk write.
- Resident report photos additionally pass `ImageRelevanceService` synchronously: screenshots/memes/documents, clear blue pool water, open water without flood evidence, and unrelated scenes are rejected and never stored. Ambiguous photos return **Uncertain** and are rejected with a request for a clearer, relevant photo. Accepted photos may carry a suggested category; a mismatch is advisory only.
- After a report is safely saved, `ImageVerificationService` runs in the background as an **advisory** authenticity check: format/extension mismatch, generator/editor markers read from the JPEG EXIF `Software` tag, stripped metadata, and dimension anomalies. Failure only sets `ImageVerificationStatus = Failed`; the report itself is never lost.
- The image pipeline was migrated off ImageSharp/Six Labors to **SkiaSharp** (`SkiaSharp` + `SkiaSharp.NativeAssets.Linux`). No Six Labors references remain.
- Covered by `COMS.Tests/ImageUploadValidatorTests.cs` (header/magic/decode/pixel-cap cases) and relevance/verification tests, alongside flood-news, 2FA, alert, retention, and chart tests.

### Flood News (keyless public feeds)

- `/FloodNews` and the dashboard `_FloodNewsPanel` aggregate GDACS RSS (Philippines flood/tropical-cyclone events only) and Philippine outlet feeds (Inquirer, Cebu Daily News, Philstar, SunStar, Rappler, Palawan News).
- Strict `FloodRelevance` filtering keeps flood-substantive, Philippines-focused stories, removes publisher "read more" boilerplate before matching, and rejects political/budget-process stories. Duplicate URLs/titles are removed before counts/trends.
- Fetch-on-demand with `IMemoryCache` (20-minute default), a 2-minute manual-refresh cooldown, and a 15-second HTTP timeout / 2MB response cap. On provider failure the last good cache is served marked stale; if none exists the UI shows an unavailable state. All external calls stay server-side; links are restricted to HTTP/HTTPS. Informational only — never used in risk scoring.

### Customer service / resident help center

- Residents can open tickets, chat in real time (`SupportHub`), close/reopen their own tickets, and see unread counts.
- CustomerService and Admin staff get a queue dashboard, status/priority filters, assignment, replies, internal notes, and close/delete actions (delete is Admin-only).
- Per-user throttles protect staff notifications: resident messages 5s / 30 per hour; ticket creation 30s / 10 per hour.

### Security, performance, and operations

- Identity password policy: min 6, at least 1 uppercase, 1 number, 1 special character; lowercase is optional (the registration/reset regexes match). Identity lockout 5 attempts / 5 minutes; auth endpoints rate-limited.
- Baseline security headers (`nosniff`, `SAMEORIGIN`, strict-origin-when-cross-origin referrer policy); forward-redirect headers honored behind Railway's TLS proxy.
- Antiforgery validation is global (`AutoValidateAntiforgeryTokenAttribute`); uploads are capped at 8MB multipart request bodies; response compression covers HTML/CSS/JS/JSON/SVG; fingerprinted static assets are cached immutably.
- Query improvements: batched latest-reading query (no per-canal N+1), indexed `(CanalId, RecordedAt)`, `(CanalId, Status)`, notification/read, and trusted-device expiry lookups.
- `/health` liveness endpoint; graceful 404 handling that keeps asset/API 404s clean instead of redirecting them to HTML.

### Cebu-only coverage (all maps locked to Cebu province)

- Bounds lat 9.30–11.45, lng 123.20–124.60; center Cebu City (10.3157, 123.8854); `maxBounds` + `minZoom: 9` on every Leaflet map. `/Reports/Create` additionally masks everything outside the Cebu report-area polygons and validates the pin against them (client + server share `wwwroot/data/cebu-report-area.geojson`).
- `/Canals/Create` + `/Canals/Edit` (Admin) — drag-and-pin picker replaces typed lat/long; "Use my location" rejects non-Cebu; server-side `ValidateCebuLocation()` rejects out-of-Cebu saves.
- `/Sensors/Create` + `/Sensors/Edit` (Admin) — same picker; pin auto-jumps to the selected canal; server-side Cebu check in `ValidateSensorAsync()`.
- `/Reports/Create` (Resident) — "Exact Location" picker replaces the free-text location box (kept as landmark detail); pin required **inside the permitted reporting area** (not merely inside the rectangular bounds): the permitted area covers Cebu Island (Cebu City, Mandaue, Talisay and every Cebu Island municipality) plus Mactan Island (Lapu-Lapu City, Cordova) and Olango Island (Lapu-Lapu City barangays); everything else (Bantayan, Camotes, Bohol, Negros, Leyte) is masked out client-side and rejected server-side by ray-casting against `wwwroot/data/cebu-report-area.geojson`; selecting a canal jumps the pin to it when the canal is in the permitted area; `Details` shows coords + mini map. New nullable `Latitude`/`Longitude` columns via `AddReportCoordinates` migration. Boundaries from OSM island relations 3604190 (Cebu), 1506969 (Mactan), 12189437 (Olango) via Nominatim `polygon_geojson` (ODbL, ~55 m tolerance).
- `/Canals/Map` overview + canal/report `Details` maps — centered on Cebu with the same bounds lock.

### Login page redesign + branding

- Split-screen navy-gradient login (`Views/Account/Login.cshtml`): Cebu-coverage badge, feature highlights, icon inputs, show/hide password, gradient Sign In; full-bleed background; `returnUrl` now preserved through login.
- Official logo: `COMS-LOGO.png` (root) copied to `wwwroot/images/` (+ transparent variant for dark surfaces) — used in sidebar, login, register, Home hero, and favicon (`wwwroot/favicon.ico` replaced).
- COMS renamed everywhere to **Canal Obstruction Maintenance System**.

### Resident simplification

- Canals tab removed from the Resident sidebar (`Views/Shared/_Sidebar.cshtml`); `/Canals`, `/Canals/Details`, `/Canals/Map` now require Admin/LGU/Barangay/Maintenance (direct-URL access denied for Residents).

### Reliability / error trapping (no UI changes)

- Model validation: `[Range]` on all coordinates/dimensions/levels, `[StringLength]` on text, required sensor type/announcement content/audience/report type.
- Controller hardening: input trimming, water-level ordering (Normal < Warning < Critical), duplicate sensor-code/email checks, role/audience/status whitelists, file type+size+extension checks with orphan cleanup, `TryParse` for user IDs, friendly FK-delete messages, fixed Edit concurrency redirect bug, fixed `LatestReadingsJson` null crash, fixed announcement image upload binding (`imageFile`), `Random.Shared` for test readings.
- New migration `AddValidationConstraints` (announcement length limits).

### Earlier baseline

- PostgreSQL migration (Npgsql; removed SQLite WAL pragma).
- Empty-by-default seeding (no demo canals / sensors).
- Login / register error trapping + lockout (5 attempts → 5-min lock).
- Solution load fix (`COMS MVC.slnx` path).

### Account management & password reset (new)

- **Forgot Password via Brevo email** — `/Account/ForgotPassword` → reset link emailed → `/Account/ResetPassword?email=...&token=...` → new password. Detailed step-by-step in §11.
- **Email-OTP registration** — `/Account/Register` creates a pending registration only → 6-digit code via Brevo → `/Account/VerifyEmail` activates the account (`Services/OtpService.cs`: 10-min expiry, 5 attempts, 60s resend cooldown, 5 sends/hour, single-use, hashed storage).
- **Remember Me** — Login checkbox issues a 7-day persistent cookie (`ExpireTimeSpan = 7 days`, sliding expiration in `Program.cs`).
- **Update Account** — `/Account/Profile` (self-service edit of name/email/phone/location + optional password change), linked from the top-navbar Profile menu.
- **Delete Account** — `/Account/Delete` (password confirmation, last-Admin guard), linked from Profile.
- **Admin user management** — `/Users` (Admin only): edit details/roles, delete other users, direct password reset (`/Users/ResetPassword/5`).

---

## 11. Forgot Password — How It Works (Detailed)

### 11.1 What the user experiences

1. On `/Account/Login`, click **Forgot password?** → `/Account/ForgotPassword` (`Views/Account/ForgotPassword.cshtml`).
2. Enter the account **email** and submit. Unknown addresses get an explicit **Email not found** error on the form (`AccountController.ForgotPassword` checks PostgreSQL via `FindByEmailAsync`); known addresses proceed to the generic confirmation page.
3. If the email is registered in PostgreSQL (`AspNetUsers`), a **reset email** arrives with a **Reset password** button (plus a plain-text URL fallback).
4. Clicking it opens `/Account/ResetPassword?email=...&token=...` (`Views/Account/ResetPassword.cshtml`) showing the email and two new-password fields.
5. Submit → password is replaced → `ResetPasswordConfirmation.cshtml` → **Sign in** with the new password.

### 11.2 How it was made (code path)

| Step | Code |
| ---- | ---- |
| Lookup | `AccountController.ForgotPassword` (POST) trims input and calls `_userManager.FindByEmailAsync(model.Email)` — Identity handles normalized-email matching against PostgreSQL, so case doesn't matter |
| Token | `_userManager.GeneratePasswordResetTokenAsync(user)` (DataProtection provider, 1-hour lifespan set in `Program.cs:55-59`) |
| Link-safe encoding | Token is Base64Url-encoded (`WebEncoders.Base64UrlEncode`) because raw Identity tokens contain `+/=` which break URLs |
| Fully-qualified link | `Url.Action(nameof(ResetPassword), "Account", new { email, token = code }, protocol: Request.Scheme)` → `https://host/Account/ResetPassword?email=...&token=...` |
| Send | `IEmailService.SendPasswordResetEmailAsync(user.Email, resetLink)` → `BrevoEmailService` (`Services/EmailService.cs`) `POST https://api.brevo.com/v3/smtp/email` with `api-key` header, JSON `{ sender, to[], subject, htmlContent }` |
| Verify | `AccountController.ResetPassword` (GET) requires both `email` + `token` or bounces to Forgot; POST Base64Url-decodes the token and calls `_userManager.ResetPasswordAsync(user, decodedToken, model.NewPassword)`; Identity errors (bad token, weak password) return as friendly validation messages |
| Safety net | Any send failure is caught: the app still redirects to the generic confirmation (no crash, no enumeration leak) and logs `SMTP Failed or Unconfigured. COPY THIS RESET LINK TO TEST: {ResetLink}` so local testing can continue without a working provider |

### 11.3 Why Brevo (HTTPS) instead of Gmail SMTP

The first implementation used Gmail SMTP (`smtp.gmail.com:587`, STARTTLS, App Password). It failed in this environment with `SMTP GeneralFailure: The operation has timed out` — the network/firewall silently drops outbound SMTP, so the handshake never completes. No code fix can unblock a filtered port. Brevo sends over **HTTPS (port 443)**, which firewalls effectively never block, using a plain `HttpClient` (15s timeout, no extra SDK package). Registration OTP (`Services/OtpService.cs` → `SendOtpEmailAsync`) uses the same provider; if Brevo is unconfigured or delivery fails, OTP issue fails loudly (no phantom code, no rate-limit consumed) so the user sees an error instead of waiting for mail that never comes. Other iterations tried along the way (direct token-in-redirect, 6-digit email/SMS OTP via Semaphore) were replaced by the current flows as the most secure standard approach.

### 11.4 Setup (to actually receive mail)

1. brevo.com → free account → SMTP & API → API Keys → create key (`xkeysib-...`).
2. Sender: verify a domain (production), or for testing use a Brevo-verified sender — free tier has daily sending limits.
3. Store secrets outside git (user-secrets overlay `appsettings.json` automatically):
   ```bash
   dotnet user-secrets set "Email:ApiKey" "xkeysib_xxx"
   dotnet user-secrets set "Email:SenderEmail" "no-reply@yourdomain.com"
   ```
   (Production/Railway equivalents: `Brevo__ApiKey`, `Brevo__SenderEmail`.)
4. Restart `dotnet run` (config binds at startup), retry Forgot Password / Register, expect `Brevo email sent to...` in console. If the key does not start with `xkeysib-`, startup logs an error — a leftover Resend `re_...` key will not work.

### 11.5 Security properties (defense points)

- **Explicit email check**: unknown addresses get an `Email not found` form error (explicit per requirements); send failures still land on the generic confirmation without crashing, logging the `COPY THIS RESET LINK` fallback.
- **Password rules visible + enforced early**: both Register and Reset require min 6 chars with at least 1 uppercase letter, 1 number, and 1 special character (lowercase is optional) — matching the Identity policy in `Program.cs` (`RequireDigit`, `RequireUppercase`, `RequireNonAlphanumeric`; `RequireLowercase` is false). Enforced by `[RegularExpression]` on the ViewModels (instant client-side feedback via `_ValidationScriptsPartial`) with a hint under each password field, so bad passwords never reach the server.
- **Time-limited, single-use tokens**: DataProtection tokens, 1-hour lifespan, consumed by `ResetPasswordAsync`.
- **No secret leaks**: API key travels only as the Brevo `api-key` header; logs record status codes and truncated bodies, never the key or full link (except the intentional local-dev `COPY THIS RESET LINK` warning).
- **Lockout still applies**: 5 failed logins → 5-minute lock; reset page enforces the same 6-char/complexity password policy as registration.

### 11.6 Troubleshooting

| Symptom | Console line | Fix |
| ------- | ------------ | --- |
| Confirmation shown, no mail | `ForgotPassword: no user found…` | Email isn't in the Postgres `comsdb` the app uses — check Admin → Users |
| Confirmation shown, no mail | `Email NOT sent (Brevo not configured)` | `Email:ApiKey`/`SenderEmail` empty — set secrets, restart |
| Error logged | `Brevo API error 401` | Bad/revoked API key (or a non-Brevo `re_...` key) |
| Error logged | `Brevo API error 422` / `400` | Unverified sender domain or invalid payload |
| Old SMTP path | `SMTP GeneralFailure: timed out` | Network blocks port 587 — the HTTPS Brevo path above avoids it |

---

## 12. Build & Tests

### Commands

```bash
# Release build of the web app
dotnet build "COMS MVC.csproj" -c Release

# Full test suite (builds the test project)
dotnet test "COMS.Tests/COMS.Tests.csproj" -c Release
```

For a faster repeat run after a build:

```bash
dotnet test "COMS.Tests/COMS.Tests.csproj" -c Release --no-build
```

### What the suite covers

`COMS.Tests/` is an **xUnit** suite targeting `net10.0`. Most tests use SQLite in-memory databases; PostgreSQL-specific persistence/query tests use a dedicated schema and honour `COMS_VERIFY_CONN` when set, falling back to the local development database. Do not commit real connection strings.

Representative areas:

- Charts: `SensorReadingsChartTests` — real readings, camelCase contract, 5-hour window, downsampling bounds/actual peaks, empty state, and the new `WaterLevelTrendJson` multi-canal ordering/no-fabrication/single-point/window tests.
- Images: `ImageUploadValidatorTests` — extensions, MIME mismatch, oversize, renamed GIF, garbage bytes, real JPEG/PNG, tiny images, pixel-budget boundaries, end-to-end `IFormFile`.
- Image intelligence: `ImageRelevanceTests`, verification/EXIF-related tests.
- Flood News: `FloodNewsCacheTests`, `FloodRelevanceTests` — cache/stale/cooldown, PH focus, flood relevance, dedup, trends.
- Alerts/retention: `AlertDeduplicationTests`, `AlertRetentionTests`, `FloodRiskRetentionTests`, `FloodRiskScoreTests`.
- Auth/security: `TwoFactorLoginTests`, `TwoFactorTrustedDeviceTests`, `OtpServiceTests`, `RegistrationValidationTests`, `RegistrationWorkflowTests`, `SecurityAuditTests`, `UserDeletionTests`, `RateThrottleTests`.
- Data/UI: `SensorReadingsFilterTests`, `QueryCountTests`, `PhTimeTests`, `CebuBoundaryTests`, `TextModerationTests`, `UiConstraintTests`, `PgPersistenceTests`.

**Current verified result (Release):** 301 passed, 0 failed, 0 skipped. Re-run the commands above after any change; do not assume this count is permanent.

---

## 13. Docker & Railway Deployment

### Docker

`Dockerfile` uses a two-stage build:

```bash
docker build -t coms-mvc .
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=...;Database=...;Username=...;Password=..." \
  coms-mvc
```

- Base/runtime: `mcr.microsoft.com/dotnet/aspnet:10.0`; build: `mcr.microsoft.com/dotnet/sdk:10.0`.
- The container exposes `8080` and runs `dotnet "COMS MVC.dll"`.
- `Program.cs` binds to `http://0.0.0.0:$PORT` when `PORT` is set (Railway/containers); locally `launchSettings.json` supplies `http://localhost:5070`.
- On startup the app applies pending EF migrations and runs the seeder. Ensure the database is reachable first.

### Railway

1. Create a PostgreSQL service and copy its connection string into `ConnectionStrings__DefaultConnection`.
2. Deploy the repository with the included `Dockerfile` (Railway injects `PORT`).
3. Set the health check path to `/health`.
4. Add the email/provider variables you actually use (see below). Never commit them.
5. Persistent uploads: container filesystems are ephemeral. Attach a volume and point the upload folders at persistent storage (or accept that uploaded images disappear on redeploy). The app recreates `wwwroot/uploads/{reports,announcements}` at runtime, but that does not make them durable across deployments.

### Environment variables (no secrets in this file)

| Variable | Purpose |
| -------- | ------- |
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string (required in production) |
| `PORT` | Container listen port injected by Railway |
| `ASPNETCORE_ENVIRONMENT` | `Development` or `Production` |
| `Brevo__ApiKey`, `Brevo__SenderEmail`, `Brevo__SenderName` | Brevo HTTPS email (preferred production shape) |
| `Email__ApiKey`, `Email__SenderEmail`, `Email__SenderName` | Legacy/local fallback names for the same values |
| `TwoFactor__TrustedDeviceDays`, `TwoFactor__CleanupIntervalMinutes` | Trusted-device lifetime (1–30 days) and cleanup cadence |
| `AlertRetention__RetentionHours`, `AlertRetention__CleanupIntervalMinutes` | Alert retention window/sweep |
| `FloodRiskRetention__RetentionHours`, `FloodRiskRetention__CleanupIntervalMinutes`, `FloodRiskRetention__HistoryPageSize`, `FloodRiskRetention__CleanupBatchSize` | Risk-history retention |
| `FloodNews__Enabled`, `FloodNews__CacheMinutes`, `FloodNews__MaxArticles`, `FloodNews__MaxAgeDays`, `FloodNews__HttpTimeoutSeconds` | Flood News provider/cache behavior |
| `ImageVerification__Enabled`, `ImageVerification__Provider` | Advisory authenticity analyzer (default `Heuristic`; `ApiKey` reserved for a future provider) |
| `ImageRelevance__Enabled` | Synchronous report-photo relevance gate |
| `TextModeration__Enabled` | Report/ticket text moderation |
| `DemoSettings__EnableSimulation`, `DemoSettings__SimulationIntervalSeconds` | Background simulated readings (keep off in production unless intentionally demoing) |
| `MapSettings__UseOpenStreetMap`, `MapSettings__MapboxToken`, `MapSettings__GoogleMapsApiKey` | Map provider settings |

Store secret values in Railway variables, user-secrets, or your host's secret store — never in `README.md`, source control, screenshots, or logs.

