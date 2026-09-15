# COMS MVC - Flood Monitoring System

A web-based flood monitoring and management system built with ASP.NET Core MVC 10, Entity Framework Core 9, and Identity for user management.

## System Overview

COMS MVC provides a centralized platform for monitoring canal water levels, detecting obstructions, assessing flood risk, and managing community reports. It supports real-time sensor data simulation with SignalR notifications.

### Features
- **Real-time monitoring** of canal sensors (water level, flow rate, debris, turbidity, temperature)
- **Automated sensor simulation** (20-second interval, disabled by default, toggleable via `appsettings.json`)
- **Obstruction alerts** with severity levels and assignment tracking
- **Flood risk assessments** with scored risk levels
- **Community reporting** system for resident-submitted concerns (with photo uploads)
- **Announcements** targeted by audience (with image uploads)
- **Notifications** via SignalR for real-time updates
- **Role-based dashboards** for Admin, LGU, Barangay, Maintenance, and Resident users
- **Hardened login/registration** with full error trapping (duplicate checks, lockout enforcement, no 500 pages on bad input)

### Architecture
- **Framework**: ASP.NET Core MVC 10 / .NET 10
- **Database**: PostgreSQL (via `Npgsql.EntityFrameworkCore.PostgreSQL` 9.0.x)
- **ORM**: Entity Framework Core 9
- **Auth**: ASP.NET Core Identity (integer-keyed users/roles), lockout enabled (5 attempts / 5 minutes)
- **Real-time**: SignalR with JSON protocol
- **Frontend**: Bootstrap 5, jQuery, Chart.js, Leaflet.js (OpenStreetMap)

### Roles & Permissions
| Role | Access |
|------|--------|
| **Admin** | Full system access; user management, announcements, all dashboards |
| **LGU** | City-level overview, announcements, reports, flood risk |
| **Barangay** | Barangay-specific canals/sensors, community reports |
| **Maintenance** | Sensor readings, alerts, task assignment, flood risk |
| **Resident** | Submit community reports, view announcements, own reports |

## Demo Accounts

Each demo account has its own unique password. Passwords are securely hashed with BCrypt via ASP.NET Core Identity's `PasswordHasher`. Accounts are seeded automatically on first run (roles + users only — no demo canals/sensors).

| Role | Email | Username | Password |
|------|-------|----------|----------|
| Admin | admin@coms.gov | admin | `Admin@COMS123!` |
| LGU | lgu@coms.gov | lguuser | `LGU@COMS123!` |
| Barangay | barangay@coms.gov | bguser | `Barangay@COMS123!` |
| Maintenance | maintenance@coms.gov | maintuser | `Maintenance@COMS123!` |
| Resident | resident@coms.gov | resident | `Resident@COMS123!` |

> **Note**: Passwords are only displayed here for development/demo purposes. In production, passwords should never be stored or displayed in plain text. Existing accounts have their passwords automatically reset on startup if the password differs from the current seed value.

## Getting Started

### Prerequisites
- .NET SDK 10.0
- PostgreSQL 17+ (with a `comsdb` database and `comsuser` login — see Database Setup below)

### Database Setup (PostgreSQL)
1. Create database `comsdb` owned by login role `comsuser`.
2. In a Query Tool on `comsdb`, run:
```sql
GRANT ALL ON SCHEMA public TO comsuser;
ALTER SCHEMA public OWNER TO comsuser;
```
3. Set the connection string in `appsettings.json`:
```json
"ConnectionStrings": {
  "DefaultConnection": "Host=localhost;Port=5432;Database=comsdb;Username=comsuser;Password=YOUR_PASSWORD_HERE"
}
```
4. Create the tables:
```bash
dotnet ef database update
```

### Run
```bash
dotnet run
```

The application starts at `http://localhost:5070` and seeds roles + demo users automatically on first run. Dashboards start at 0 until data is added through the UI.

### Database Migration
```bash
# After model changes: regenerate and apply
dotnet ef migrations add <Name>
dotnet ef database update
```

Migrations are located in `Migrations/`. The `ApplicationDbContext` is configured in `Data/ApplicationDbContext.cs`.

### Remote access (second machine on the same network)
1. `postgresql.conf`: `listen_addresses = '*'`
2. `pg_hba.conf`: `host all all 192.168.1.0/24 scram-sha-256` (match your LAN subnet)
3. Reload: `SELECT pg_reload_conf();`
4. Allow TCP 5432 in Windows Firewall (scoped to LocalSubnet)
5. Point the other machine's connection string at `Host=<this-PC-IP>`

## Configuration

### appsettings.json
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=comsdb;Username=comsuser;Password=YOUR_PASSWORD_HERE"
  },
  "MapSettings": {
    "UseOpenStreetMap": true,
    "MapboxToken": "",
    "GoogleMapsApiKey": ""
  },
  "DemoSettings": {
    "EnableSimulation": false,
    "SimulationIntervalSeconds": 20
  }
}
```

## Project Structure
```
COMS-MVC/
├── Controllers/        # MVC controllers (Account, Dashboard, Canals, Sensors, etc.)
├── Data/               # ApplicationDbContext
├── Migrations/         # EF Core migrations (PostgreSQL)
├── Models/             # Domain entities and ViewModels
├── Services/           # App services (DataSeeder, SensorService, AlertService, etc.)
├── Hubs/               # SignalR hubs for real-time updates
├── Views/              # Razor views (role-specific dashboards, CRUD views)
├── wwwroot/            # Static assets + uploads/ (announcement/report images live on disk)
└── Program.cs          # App startup configuration
```

## Services
- **DataSeeder** — Seeds roles + 5 demo users on startup (no demo canals/sensors; dashboards start at 0)
- **SensorSimulationService** — Background service generating simulated readings (disabled by default)
- **FloodRiskService** — Computes flood risk scores per canal based on sensor data
- **AlertService** — Detects obstructions and creates/updates alerts with severity levels
- **NotificationService** — Creates and dispatches user notifications

## Recent Updates
- **PostgreSQL migration** — Switched from SQLite (`coms.db`) to PostgreSQL via Npgsql; regenerated the initial migration; removed the SQLite-only `PRAGMA journal_mode=WAL` startup line.
- **Empty-by-default seeding** — Removed the hardcoded demo canals/sensors from `DataSeeder`; fresh installs show 0 across all dashboard cards until the user adds input. Simulation is off by default.
- **Login/register error trapping** — Null guards, input trimming, try/catch on all paths (no more 500 pages); field-level errors for duplicate username/email; Identity password errors mapped to the Password field; unknown roles fall back to Resident; failed role assignment rolls back the half-created account.
- **Lockout enforcement** — Login now uses `CheckPasswordSignInAsync` with lockout (5 failed attempts → temporary lock) plus explicit locked-out / not-allowed messages; fixed a missing `SignInAsync` so successful logins actually persist.
- **Solution load fix** — Fixed `COMS MVC.slnx` pointing at a non-existent `COMS MVC/COMS MVC.csproj` path so Solution Explorer loads.
