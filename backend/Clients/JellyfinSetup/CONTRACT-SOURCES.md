# Jellyfin 10.11 setup contract

Validated against the official Jellyfin `v10.11.8` source (the fixture server reports `10.11.8`; the client accepts only the `10.11.*` minor line).

| Operation | Route and wire contract | Official source |
| --- | --- | --- |
| Readiness | `GET /health`, successful response means ready | [Startup.cs health-check mapping](https://github.com/jellyfin/jellyfin/blob/v10.11.8/Jellyfin.Server/Startup.cs) |
| Login | `POST /Users/AuthenticateByName`, `{ "Username": ..., "Pw": ... }`; response `AccessToken`; `X-Emby-Authorization: MediaBrowser ...` | [UserController.cs](https://github.com/jellyfin/jellyfin/blob/v10.11.8/Jellyfin.Api/Controllers/UserController.cs), [AuthenticationRequest.cs](https://github.com/jellyfin/jellyfin/blob/v10.11.8/MediaBrowser.Controller/Session/AuthenticationRequest.cs) |
| Startup bootstrap | `POST /Startup/Configuration`; `POST /Startup/User` with `{ Name, Password }`; `POST /Startup/RemoteAccess`; `POST /Startup/Complete` | [StartupController.cs](https://github.com/jellyfin/jellyfin/blob/v10.11.8/Jellyfin.Api/Controllers/StartupController.cs) |

### Recovery boundary

Stock Jellyfin 10.11.8 returns the bearer `AccessToken` at authentication time. It does **not** expose bearer tokens in `SessionInfoDto` or `/Sessions` metadata; those responses are useful only for non-secret session metadata. Recovery therefore uses the durable token journal and same-device reauthentication. Reauthentication with the same `DeviceId` replaces the prior Jellyfin session; tests prove token liveness through `/Users/Me`, never by fabricating a token from `/Sessions`.
| API key | `GET /Auth/Keys`; `POST /Auth/Keys?app=<name>` returns `204` with no body; `DELETE /Auth/Keys/{key}` returns `204` | [ApiKeyController.cs](https://github.com/jellyfin/jellyfin/blob/v10.11.8/Jellyfin.Api/Controllers/ApiKeyController.cs) |
| Libraries | `GET /Library/VirtualFolders`; `POST /Library/VirtualFolders?name=...&collectionType=movies|tvshows&paths=...&refreshLibrary=false`, optional `AddVirtualFolderDto` body; create returns `204` | [LibraryStructureController.cs](https://github.com/jellyfin/jellyfin/blob/v10.11.8/Jellyfin.Api/Controllers/LibraryStructureController.cs), [AddVirtualFolderDto.cs](https://github.com/jellyfin/jellyfin/blob/v10.11.8/Jellyfin.Api/Models/LibraryStructureDto/AddVirtualFolderDto.cs) |
| Plugin | `GET /Plugins`; configuration `GET|POST /Plugins/{pluginId}/Configuration`, POST returns `204` | [PluginsController.cs](https://github.com/jellyfin/jellyfin/blob/v10.11.8/Jellyfin.Api/Controllers/PluginsController.cs) |
| Scheduled sync | `GET /ScheduledTasks`; select `Key == NzbdavLibrarySync`; `POST /ScheduledTasks/Running/{id}` returns `204` | [ScheduledTasksController.cs](https://github.com/jellyfin/jellyfin/blob/v10.11.8/Jellyfin.Api/Controllers/ScheduledTasksController.cs) |
| Verification | Re-reads health, active keys, plugin/config, virtual folders, and scheduled tasks | Same sources above |
