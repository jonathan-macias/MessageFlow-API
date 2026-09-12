# MessageFlow

WhatsApp bulk messaging automation platform. Import contact data from Excel, define automated workflows with dynamic filters and personalized message templates, and send WhatsApp messages at scale — on schedule or triggered by data events.

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10.0 |
| Web | ASP.NET Core Web API |
| ORM | Entity Framework Core 10.x |
| Database | PostgreSQL 18 |
| Auth | JWT Bearer + Google OAuth |
| Messaging | Evolution API (WhatsApp gateway) |
| AI | Google Gemini (message generation, NL dataset queries) |
| Excel | MiniExcel |
| Architecture | Clean Architecture + Custom CQRS |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PostgreSQL 18
- [Evolution API](https://github.com/EvolutionAPI/evolution-api) instance (WhatsApp gateway)
- Google Cloud project (OAuth credentials)
- Google AI Studio API key (Gemini)

## Getting Started

### 1. Database

Create the PostgreSQL database and run migrations:

```bash
cd src

dotnet ef migrations add InitialMigration \
  --project ./MessageFlow.Infrastructure \
  --startup-project ./MessageFlow.Api

dotnet ef database update \
  --project ./MessageFlow.Infrastructure \
  --startup-project ./MessageFlow.Api
```

Or target a remote database directly:

```bash
dotnet ef database update \
  --project ./MessageFlow.Infrastructure \
  --startup-project ./MessageFlow.Api \
  --connection "Host=YOUR_HOST;Port=5432;Database=messageflow;Username=YOUR_USER;Password=YOUR_PASSWORD"
```

### 2. Configuration

Copy the example settings and fill in your credentials:

```bash
cp src/MessageFlow.Api/appsettings.example.json src/MessageFlow.Api/appsettings.json
```

Edit `appsettings.json` and configure each section:

#### PostgreSQL

```json
"ConnectionStrings": {
  "Default": "Host=localhost;Port=5432;Database=messageflow;Username=YOUR_USER;Password=YOUR_PASSWORD"
}
```

#### Evolution API (WhatsApp)

Spin up an Evolution API instance and create a WhatsApp connection:

```json
"EvolutionApi": {
  "BaseUrl": "http://YOUR_HOST:8080",
  "Instance": "YOUR_INSTANCE_NAME",
  "GlobalApiKey": "YOUR_GLOBAL_API_KEY",
  "TimeoutSeconds": 30
}
```

#### JWT Authentication

Generate a strong secret (at least 32 characters):

```json
"Jwt": {
  "Issuer": "MessageFlow",
  "Audience": "MessageFlow",
  "Secret": "REPLACE_WITH_A_STRONG_SECRET",
  "ExpirationMinutes": 60
}
```

#### Google OAuth

Create an OAuth 2.0 client ID in [Google Cloud Console](https://console.cloud.google.com/apis/credentials):

```json
"Authentication": {
  "Google": {
    "ClientId": "YOUR_CLIENT_ID.apps.googleusercontent.com",
    "ClientSecret": "YOUR_CLIENT_SECRET"
  }
}
```

#### Google Gemini

Create an API key in [Google AI Studio](https://aistudio.google.com):

```json
"Gemini": {
  "ApiKey": "YOUR_GEMINI_API_KEY",
  "Model": "gemini-3.5-flash-lite"
}
```

### 3. Run

```bash
cd src/MessageFlow.Api
dotnet run
```

The API is available at `http://localhost:5000` (or the port configured in `launchSettings.json`). Swagger UI is served from the root.

## API Overview

### Authentication

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/auth/register` | Register (email + password) |
| POST | `/api/auth/login` | Login, returns JWT |
| POST | `/api/auth/google` | Google OAuth login |

### Datasets

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/datasets/upload` | Import Excel file (multipart, 30 MB max) |
| GET | `/api/datasets` | List datasets |
| GET | `/api/datasets/{id}` | Dataset detail |
| GET | `/api/datasets/{id}/columns` | Column definitions |
| GET | `/api/datasets/{id}/rows` | Paginated rows |
| PUT | `/api/datasets/{id}/phone-column` | Set phone column |

### Flows

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/flows` | Create Flow (Draft) |
| GET | `/api/flows` | List Flows |
| GET | `/api/flows/{id}` | Flow detail |
| PUT | `/api/flows/{id}` | Update name/description/schedule |
| PUT | `/api/flows/{id}/message` | Update message template |
| PUT | `/api/flows/{id}/filter` | Set/clear root filter |
| DELETE | `/api/flows/{id}` | Delete (Draft/Archived only) |
| POST | `/api/flows/{id}/activate` | Activate Flow |
| POST | `/api/flows/{id}/pause` | Pause Flow |
| POST | `/api/flows/{id}/archive` | Archive Flow |
| POST | `/api/flows/{id}/execute` | Send Now (returns 202) |
| POST | `/api/flows/{id}/preview` | Preview rendered message |
| POST | `/api/flows/{id}/filters/preview` | Preview which rows match a filter |
| GET | `/api/flows/{id}/executions` | List executions |

### Flow Executions

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/flow-executions/{id}` | Execution detail + per-recipient results |

### AI

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/ai/messages/generate` | Generate message template from description |
| POST | `/api/ai/datasets/query` | Query dataset with natural language |

## Key Concepts

### Flow

The central automation unit. Combines:

- **Message Template** — text with `{{ColumnName}}` placeholders
- **Dataset** — contact data imported from Excel
- **Phone Column** — which column contains recipient numbers
- **Schedule** — when to execute:
  - `Immediate` — manual send only
  - `OneTime` — run at a specific UTC datetime
  - `Recurring` — daily recurrence with interval
  - `DataTriggered` — fire when a date column matches (e.g., birthdays)
- **Root Filter** — optional AND/OR tree to target specific rows
- **State Machine** — Draft → Active → Paused → Completed → Archived

### Filter Tree

Recursive filter structure supporting nested AND/OR groups (up to 10 levels):

- **Text** — Equals, Contains, StartsWith, EndsWith
- **Number** — Equals, GreaterThan, LessThan, Between
- **Date** — Before, After, Between
- **Boolean** — Equals
- **Null checks** — IsNull, IsNotNull

### Execution Engine

Processes Flow executions row-by-row:

1. Streams all dataset rows through `FilterMatcher`
2. Renders the message template per row
3. Sends via Evolution API (WhatsApp)
4. Registers per-recipient results (Succeeded / Failed / Skipped)
5. Updates execution counters and status

### Multi-Tenancy

Every Dataset and Flow is scoped to the creating user via `OwnerId`.

## Project Structure

```
src/
  MessageFlow.Domain/           # Pure domain model (no dependencies)
  MessageFlow.Application/      # CQRS handlers, validators, execution logic
  MessageFlow.Infrastructure/   # EF Core, PostgreSQL, Evolution API client, Gemini AI
  MessageFlow.Api/              # Controllers, auth, DI composition root
  MessageFlow.Tests/            # Unit tests
```

## License

MIT
