# COMS MVC - Canal Obstruction Maintenance System (Flood Monitoring)

A web-based canal flood monitoring and management system built with **ASP.NET Core MVC (.NET 10)**, **Entity Framework Core 9 + PostgreSQL**, **ASP.NET Core Identity**, and **SignalR** for real-time updates.

> Defense-ready: every Model, Controller, View/UI URL, role access, and demo account location is documented below.

Base URL (development): `http://localhost:5070`

---

## 1. System Overview

COMS centralizes:

- Canal inventory with map coordinates + water-level thresholds
- Sensor registry + sensor readings (water level, flow rate, debris, turbidity, temperature)
- Automatic obstruction alerts with severity + assignment
- Flood risk assessments (rule-based score per canal)
- Community reports from residents (with photo upload)
- Announcements with target audience (with image upload)
- Real-time notifications via SignalR
- Role-based dashboards for 5 roles

### Tech Stack

| Layer     | Technology / File                                              |
| --------- | -------------------------------------------------------------- |
| Framework | ASP.NET Core MVC 10 / .NET 10 — `Program.cs`, `COMS MVC.csproj` |
| Database  | PostgreSQL 17+ via `Npgsql.EntityFrameworkCore.PostgreSQL` 9.x |
| ORM       | EF Core 9 — `Data/ApplicationDbContext.cs`, `Migrations/`      |
| Auth      | ASP.NET Core Identity (int keys), lockout 5 attempts / 5 min   |
| Real-time | SignalR + JSON — `Hubs/MonitoringHub.cs`, route `/monitoringhub` |
| Frontend  | Bootstrap 5, jQuery, Chart.js, Leaflet.js (OpenStreetMap)      |
| Uploads   | Disk storage under `wwwroot/uploads/`                          |

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
├── Program.cs                    # Startup: DB, Identity, cookies, DI, SignalR, routing, seeder
├── appsettings.json              # Connection string, MapSettings, DemoSettings
├── Data/
│   └── ApplicationDbContext.cs   # All DbSets + Fluent API relationships
├── Models/                       # Entities + ViewModels (see §3)
├── Controllers/                  # 12 controllers (see §4)
├── Views/                        # Razor pages per controller + Shared layout (see §5)
├── Services/
│   ├── DataSeeder.cs             # Roles + 5 demo users (temporary accounts live here)
│   ├── SensorService.cs          # Sensor CRUD helpers
│   ├── AlertService.cs           # Obstruction detection
│   ├── FloodRiskService.cs       # Risk scoring
│   ├── NotificationService.cs    # Notifications
│   └── SensorSimulationService.cs# Background simulated readings (off by default)
├── Hubs/
│   └── MonitoringHub.cs          # SignalR hub: user, role, canal groups
├── Migrations/                   # EF Core PostgreSQL migrations (InitialCreate, AddValidationConstraints, AddReportCoordinates)
├── wwwroot/
│   ├── images/COMS-LOGO.png      # System logo (transparent variant: COMS-LOGO-transparent.png)
│   ├── uploads/announcements/    # Created at runtime — announcement images
│   └── uploads/reports/          # Created at runtime — community report photos
├── COMS-LOGO.png                 # Source logo file (project root, copied into wwwroot/images/)
└── COMS MVC.slnx / COMS MVC.csproj
```

---

## 3. Models (Database Entities)

Location: `Models/` — Tables created via `Migrations/` — DbSets in `Data/ApplicationDbContext.cs:14-28`.

`*` = `[Required]` in code.

### 3.1 ApplicationUser — `Models/User.cs` → `AspNetUsers`

Identity user (int key). Extra fields: `FullName*`, `Barangay`, `City`, `Address`, `CreatedAt`.

Related tables: `AspNetRoles`, `AspNetUserRoles`, claims/logins.

Has many: CommunityReports, Notifications, PostedAnnouncements.

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
| `CanalId` | FK → Canals |
| `ReportType`, `Title*`, `Description` | Content |
| `PhotoPath` | e.g. `/uploads/reports/abc.jpg` |
| `Location` | Landmark / street detail (free text) |
| `Latitude?`, `Longitude?` | Map pin from resident picker — must be inside Cebu (added via `AddReportCoordinates` migration) |
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

### 3.10 ViewModels (not tables)

- `Models/AccountViewModels.cs`:
  - `LoginViewModel`: `Username`, `Password`, `RememberMe`
  - `RegisterViewModel`: `FullName*`, `UserName*`, `Email*`, `PhoneNumber*`, `Password* (min 6)`, `ConfirmPassword*`, `Barangay?`, `City?`, `Address?`, `Role = Resident`
  - `ForgotPasswordViewModel`: `Email*` (email format validated)
  - `AccountResetPasswordViewModel`: `Email*`, `Token*` (Base64Url Identity token from the email link), `NewPassword* (min 6)`, `ConfirmPassword*` (must match `NewPassword`)
  - `ProfileViewModel`: `UserName` (display only, immutable), `FullName*`, `Email*`, `PhoneNumber?`, `Barangay?`, `City?`, `Address?`, `CurrentPassword?` + `NewPassword?` (optional password change)
  - `DeleteAccountViewModel`: `UserName`, `Email` (display), `Password*` (confirmation)
- `Models/DashboardViewModel.cs`: aggregated counts (`TotalCanals`, `TotalSensors`, `ActiveAlerts`, `PendingReports`, `HighRiskCanals`, `RegisteredUsers`, `Online/OfflineSensors`) + lists (`RecentAlerts`, `RecentReports`, `RecentRiskAssessments`, `CanalStatusOverview`, `SensorStatus`, `LatestReadings`, `Announcements`) + `CurrentRole`, `UserName`, `UserBarangay`, `UnreadNotifications`. Built in `DashboardController.cs:78-170`.

---

## 4. Controllers — `Controllers/`

### 4.1 AccountController — `Controllers/AccountController.cs`

Base: `/Account`

| URL | View File | Access |
| --- | --------- | ------ |
| `GET + POST /Account/Login` | `Views/Account/Login.cshtml` | Anonymous |
| `GET + POST /Account/Register` | `Views/Account/Register.cshtml` | Anonymous |
| `GET + POST /Account/ForgotPassword` | `Views/Account/ForgotPassword.cshtml` | Anonymous |
| `GET /Account/ForgotPasswordConfirmation` | `Views/Account/ForgotPasswordConfirmation.cshtml` | Anonymous |
| `GET + POST /Account/ResetPassword?email=...&token=...` | `Views/Account/ResetPassword.cshtml` | Anonymous |
| `GET /Account/ResetPasswordConfirmation` | `Views/Account/ResetPasswordConfirmation.cshtml` | Anonymous |
| `GET + POST /Account/Profile` (update own account) | `Views/Account/Profile.cshtml` | Logged in |
| `GET + POST /Account/Delete` (delete own account) | `Views/Account/Delete.cshtml` | Logged in |
| `POST /Account/Logout` | — (redirect to Login) | Logged in |

Notes: login accepts username **or** email (+ Remember Me checkbox → 7-day persistent cookie, sliding expiration); register role dropdown is Resident-only (`AccountController.cs`); unknown roles fall back to Resident; failed role assignment rolls back the user. Forgot Password is email-based via the Resend HTTPS API — unknown emails show an explicit not-found error — full step-by-step in §11. Register and Reset enforce the Identity password policy up front (min 6, upper/lower/number/special) with hints under each field. `Profile` edits FullName/Email/Phone/Barangay/City/Address plus optional password change (current + new); `Delete` requires password confirmation and blocks deleting the last remaining Admin. Top navbar Profile links to `/Account/Profile`; Login page links to Forgot Password.

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
| `GET /SensorReadings` (filter by canal / sensor / date) | Admin, LGU, Barangay, Maintenance |
| `GET /SensorReadings/Details/5` | Admin, LGU, Barangay, Maintenance |

No manual create — rows come from simulation or service layer. Residents excluded.

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

### 4.12 HomeController — `Controllers/HomeController.cs`

Public pages (not the app default):

| URL | View File |
| --- | --------- |
| `GET /Home/Index` | `Views/Home/Index.cshtml` |
| `GET /Home/Privacy` | `Views/Home/Privacy.cshtml` |

SignalR hub: `Hubs/MonitoringHub.cs` at `/monitoringhub` (`[Authorize]`, groups `user_{id}`, `role_{role}`, `canal_{id}`).

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

### LGU

Landing: `/Dashboard/Lgu` (`Views/Dashboard/Lgu.cshtml`)

Demo these:

- `/Dashboard/Lgu` (city overview cards)
- `/FloodRisk` (city risk)
- `/Alerts`
- `/Reports` (review)
- `/Announcements` (post city-wide)
- `/Canals/Map`

### Barangay

Landing: `/Dashboard/Barangay` (`Views/Dashboard/Barangay.cshtml`)

Demo these:

- `/Dashboard/Barangay` (barangay canals / sensors)
- `/Reports` (own-barangay reports)
- `/Alerts`
- `/Announcements` (read)
- `/SensorReadings`

### Maintenance

Landing: `/Dashboard/Maintenance` (`Views/Dashboard/Maintenance.cshtml`)

Demo these:

- `/Alerts` (Acknowledge → In Progress → Resolved)
- `/Sensors` (status)
- `/SensorReadings`
- `/FloodRisk`
- `/Reports` (field verification)

### Resident

Landing: `/Dashboard/Resident` (`Views/Dashboard/Resident.cshtml`)

Demo these (no Canals tab — removed for Residents):

- `/Reports/Create` (submit + photo + Cebu map pin)
- `/Reports/MyReports` (track Pending → Under Review → Resolved)
- `/Announcements` (read)
- `/Notifications` (my inbox)

### All users

- `/Account/Login` — accepts username **or** email
- `/Account/Register` — creates Resident only (staff accounts come from seeder or Admin → Users)

> Defense tip: start at `/Account/Login` → Admin → `/Dashboard/Admin` cards (0 on fresh DB) → `Canals/Create` → `Sensors/Create` → Resident `Reports/Create` → Maintenance `Alerts` → `FloodRisk` → LGU `Announcements/Create` → bell notification.

---

## 6. Temporary / Demo Accounts — Where They Are Stored

**There is no password file. Accounts are seeded into PostgreSQL on startup.**

| Question | Answer |
| -------- | ------ |
| Seed code file | `Services/DataSeeder.cs` — `EnsureRolesAsync()` creates 5 roles; `SeedUsersAsync()` creates 5 demo users |
| When does it run? | Every startup in `Program.cs` — `db.Database.Migrate()` then `await seeder.SeedAsync()` |
| Where in DB? | PostgreSQL `comsdb` (see `appsettings.json`), tables `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles` |
| Demo data? | No demo canals / sensors — dashboards start at 0 |
| Password storage | Hashed via `UserManager.CreateAsync`. Existing emails get password reset each startup |
| Plain password visible? | Only in `Services/DataSeeder.cs` + table below (dev/demo only) |
| Lockout | 5 failed attempts → 5-min lock (`Program.cs`, enforced in `AccountController.cs`) |

### Demo Accounts

| Role | Email | Username | Password | Full Name |
| ---- | ----- | -------- | -------- | --------- |
| Admin | admin@coms.gov | admin | `Admin@COMS123!` | COMS Admin |
| LGU | lgu@coms.gov | lguuser | `LGU@COMS123!` | LGU Officer |
| Barangay | barangay@coms.gov | bguser | `Barangay@COMS123!` | Barangay Captain |
| Maintenance | maintenance@coms.gov | maintuser | `Maintenance@COMS123!` | Maintenance Lead |
| Resident | resident@coms.gov | resident | `Resident@COMS123!` | Juan Dela Cruz |

Registered (non-demo) users: created via `/Account  /Register` → same `AspNetUsers` table with role `Resident`; manage at `/Users` (Admin only).

---

## 7. Services, Uploads, and Config Files

| Item | File | Notes |
| ---- | ---- | ----- |
| DataSeeder | `Services/DataSeeder.cs` | Seeds roles + users only |
| Email (Resend) | `Services/EmailService.cs` (`IEmailService`, `ResendEmailService`, `ResendOptions`) | Transactional email over HTTPS for password resets (see §11); plain `HttpClient`, no SDK package |
| SensorService | `Services/SensorService.cs` | Sensor helpers |
| AlertService | `Services/AlertService.cs` | Creates / updates alerts with severity |
| FloodRiskService | `Services/FloodRiskService.cs` | Rule-based score → `FloodRiskAssessments` |
| NotificationService | `Services/NotificationService.cs` | Inbox + unread count + SignalR dispatch |
| SensorSimulationService | `Services/SensorSimulationService.cs` | Background readings every 20s; disabled by default |
| Report uploads | `Controllers/ReportsController.cs` | → `wwwroot/uploads/reports/` |
| Announcement uploads | `Controllers/AnnouncementsController.cs` | → `wwwroot/uploads/announcements/` |
| DB context | `Data/ApplicationDbContext.cs` | 8 DbSets + Fluent API, unique `SensorCode`, indexes, Restrict / SetNull deletes (see §8 for full DB write-up) |
| Startup | `Program.cs` | DI, Identity, cookies, SignalR, antiforgery, migrate + seed, 404 handler |

Upload folders are auto-created at runtime (`Directory.CreateDirectory`). DB stores the `/uploads/...` path (`PhotoPath` / `ImagePath`).

`appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=comsdb;Username=comsuser;Password=YOUR_PASSWORD_HERE"
  },
  "MapSettings": { "UseOpenStreetMap": true, "MapboxToken": "", "GoogleMapsApiKey": "" },
  "DemoSettings": { "EnableSimulation": false, "SimulationIntervalSeconds": 20 },
  "Email": { "ApiKey": "", "SenderEmail": "", "SenderName": "COMS" }
}
```

`Email` feeds `ResendOptions` (`Program.cs:48-53`) for password-reset delivery (see §11). The real API key is **never committed** — stored in user-secrets (`dotnet user-secrets set "Email:ApiKey" "re_xxx"`) or `Email__ApiKey` env vars, which overlay the file at runtime.

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
| Context | `Data/ApplicationDbContext.cs` (Identity + 8 `DbSet`s) |
| Migrations | `Migrations/` — `InitialCreate`, `AddValidationConstraints`, `AddReportCoordinates` |

### 8.2 Connection string (`appsettings.json:2-4`)

```json
"ConnectionStrings": {
  "DefaultConnection": "Host=localhost;Port=5432;Database=comsdb;Username=comsuser;Password=YOUR_PASSWORD_HERE"
}
```

Parts: `Host` = DB server (use the PC's LAN IP for a second machine), `Port` = `5432` (PostgreSQL default), `Database` = `comsdb`, `Username`/`Password` = `comsuser` credentials.

### 8.3 How the app connects (`Program.cs:10-14`)

```csharp
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
```

On every startup (`Program.cs:62-69`) the app runs `db.Database.Migrate()` (auto-applies pending migrations) then `IDataSeeder.SeedAsync()` (creates the 5 roles + 5 demo users — see §6). So after `dotnet ef database update`, just `dotnet run` is enough; the schema stays current automatically.

### 8.4 Tables created by `ApplicationDbContext`

Identity: `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoleClaims`. Domain: `Canals`, `Sensors`, `SensorReadings`, `ObstructionAlerts`, `CommunityReports` (+ nullable `Latitude`/`Longitude` from `AddReportCoordinates`), `FloodRiskAssessments`, `Notifications`, `Announcements`, plus `__EFMigrationsHistory`.

Key constraints (Fluent API in `ApplicationDbContext.cs:30-149`): unique `Sensors.SensorCode`, indexes on `RecordedAt`/`DetectedAt`/`CreatedAt`/`AssessmentDate`, `DeleteBehavior.Restrict` on parent links (deleting a canal/sensor with children fails with a friendly message instead of cascading), `SetNull` for optional alert links.

### 8.5 Setup from scratch (for panel / reproduction)

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

5. Verify in pgAdmin/psql: tables listed under `comsdb > Schemas > public > Tables`, and `__EFMigrationsHistory` shows 3 applied migrations.

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

1. **Login** (`/Account/Login`) — validation, lockout message, role redirect.
2. **Admin dashboard** (`/Dashboard/Admin`) — `DashboardViewModel` counts + `BuildDashboardViewModelAsync`.
3. **Canals + Map** (`/Canals`, `/Canals/Map`) — Cebu-locked drag-and-pin picker on Create; `Models/Canal.cs` thresholds + Leaflet view.
4. **Sensors + Readings** (`/Sensors`, `/SensorReadings`) — same Cebu picker on sensor Create (pin follows canal); `Models/Sensor.cs` / `SensorReading.cs`.
5. **Resident report** (Resident → `/Reports/Create` with photo + Cebu map pin → `wwwroot/uploads/reports/`) — `Models/CommunityReport.cs` (`Latitude`/`Longitude` + mini map on Details).
6. **Alert triage** (Maintenance → `/Alerts` acknowledge / resolve) — `AlertService.cs` + `Models/ObstructionAlert.cs`.
7. **Flood risk** (`/FloodRisk`) — `FloodRiskService.cs` + `Models/FloodRiskAssessment.cs`.
8. **Announcement + Notification** (`/Announcements/Create` as LGU → bell via `/monitoringhub`) — `MonitoringHub.cs` + `NotificationService.cs`.
9. **Users + Seeder** (`/Users` + `Services/DataSeeder.cs`) — "where are temporary accounts stored?" → DB tables + seed file.

---

## 10. Latest Features & Recent Updates

### Cebu-only coverage (all maps locked to Cebu province)

- Bounds lat 9.30–11.45, lng 123.20–124.60; center Cebu City (10.3157, 123.8854); `maxBounds` + `minZoom: 9` on every Leaflet map.
- `/Canals/Create` + `/Canals/Edit` (Admin) — drag-and-pin picker replaces typed lat/long; "Use my location" rejects non-Cebu; server-side `ValidateCebuLocation()` rejects out-of-Cebu saves.
- `/Sensors/Create` + `/Sensors/Edit` (Admin) — same picker; pin auto-jumps to the selected canal; server-side Cebu check in `ValidateSensorAsync()`.
- `/Reports/Create` (Resident) — "Exact Location" picker replaces the free-text location box (kept as landmark detail); pin required inside Cebu; selecting a canal jumps the pin to it; `Details` shows coords + mini map. New nullable `Latitude`/`Longitude` columns via `AddReportCoordinates` migration.
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

- **Forgot Password via Resend email** — `/Account/ForgotPassword` → reset link emailed → `/Account/ResetPassword?email=...&token=...` → new password. Detailed step-by-step in §11.
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
| Send | `IEmailService.SendPasswordResetEmailAsync(user.Email, resetLink)` → `ResendEmailService` (`Services/EmailService.cs`) `POST https://api.resend.com/emails` with `Authorization: Bearer <ApiKey>`, JSON `{ from, to[], subject, html }` |
| Verify | `AccountController.ResetPassword` (GET) requires both `email` + `token` or bounces to Forgot; POST Base64Url-decodes the token and calls `_userManager.ResetPasswordAsync(user, decodedToken, model.NewPassword)`; Identity errors (bad token, weak password) return as friendly validation messages |
| Safety net | Any send failure is caught: the app still redirects to the generic confirmation (no crash, no enumeration leak) and logs `SMTP Failed or Unconfigured. COPY THIS RESET LINK TO TEST: {ResetLink}` so local testing can continue without a working provider |

### 11.3 Why Resend (HTTPS) instead of Gmail SMTP

The first implementation used Gmail SMTP (`smtp.gmail.com:587`, STARTTLS, App Password). It failed in this environment with `SMTP GeneralFailure: The operation has timed out` — the network/firewall silently drops outbound SMTP, so the handshake never completes. No code fix can unblock a filtered port. Resend sends over **HTTPS (port 443)**, which firewalls effectively never block, using a plain `HttpClient` (`Program.cs:49-53`, 15s timeout, no extra SDK package). Other iterations tried along the way (direct token-in-redirect, 6-digit email/SMS OTP via Semaphore) were replaced by the link flow as the most secure standard approach.

### 11.4 Setup (to actually receive mail)

1. resend.com → free account → API Keys → create key (`re_...`).
2. Sender: verify a domain (production), or for testing use `onboarding@resend.dev` — free tier only delivers to your Resend account's own email address.
3. Store secrets outside git (user-secrets overlay `appsettings.json` automatically):
   ```bash
   dotnet user-secrets set "Email:ApiKey" "re_xxx"
   dotnet user-secrets set "Email:SenderEmail" "onboarding@resend.dev"
   ```
4. Restart `dotnet run` (config binds at startup), retry Forgot Password, expect `Resend email sent to...` in console.

### 11.5 Security properties (defense points)

- **Explicit email check**: unknown addresses get an `Email not found` form error (explicit per requirements); send failures still land on the generic confirmation without crashing, logging the `COPY THIS RESET LINK` fallback.
- **Password rules visible + enforced early**: both Register and Reset require min 6 chars with 1 uppercase, 1 lowercase, 1 number, 1 special character — matching the Identity policy in `Program.cs` (`RequireDigit/Uppercase/Lowercase/NonAlphanumeric`). Enforced by `[RegularExpression]` on the ViewModels (instant client-side feedback via `_ValidationScriptsPartial`) with a hint under each password field, so bad passwords never reach the server.
- **Time-limited, single-use tokens**: DataProtection tokens, 1-hour lifespan, consumed by `ResetPasswordAsync`.
- **No secret leaks**: API key travels only as a Bearer header; logs record status codes and truncated bodies, never the key or full link (except the intentional local-dev `COPY THIS RESET LINK` warning).
- **Lockout still applies**: 5 failed logins → 5-minute lock; reset page enforces the same 6-char/complexity password policy as registration.

### 11.6 Troubleshooting

| Symptom | Console line | Fix |
| ------- | ------------ | --- |
| Confirmation shown, no mail | `ForgotPassword: no user found…` | Email isn't in the Postgres `comsdb` the app uses — check Admin → Users |
| Confirmation shown, no mail | `Email NOT sent (Resend not configured)` | `Email:ApiKey`/`SenderEmail` empty — set secrets, restart |
| Error logged | `Resend API error 401` | Bad/revoked API key |
| Error logged | `Resend API error 422` | Unverified sender domain, or `onboarding@` sent to non-account email |
| Old SMTP path | `SMTP GeneralFailure: timed out` | Network blocks port 587 — Resend (above) is the fix |
