using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// Owns all VRChat group-related message handling and the groups cache refresh.

public class GroupsController
{
    private readonly CoreLibrary _core;

    public GroupsController(CoreLibrary core)
    {
        _core = core;
    }

    // Cache fetch

    public async Task FetchAndCacheAsync()
    {
        try
        {
            var groups = await _core.VrcApi.GetUserGroupsAsync();
            var ids = groups.Cast<JObject>()
                .Select(g => g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var fullGroups = await Task.WhenAll(ids.Select(id => _core.VrcApi.GetGroupAsync(id)));

            var enriched = new List<object>();
            for (int i = 0; i < ids.Count; i++)
            {
                var full = fullGroups[i];
                if (full == null) continue;

                var myMember = full["myMember"] as JObject;
                var perms = myMember?["permissions"]?.ToObject<List<string>>();
                var name = full["name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(name)) continue;

                var canCreate = perms == null
                    || perms.Contains("*")
                    || perms.Contains("group-instance-open-create")
                    || perms.Contains("group-instance-plus-create")
                    || perms.Contains("group-instance-public-create")
                    || perms.Contains("group-instance-restricted-create");

                var canPost  = perms != null && (perms.Contains("*") || perms.Contains("group-announcement-manage"));
                var canEvent = perms != null && (perms.Contains("*") || perms.Contains("group-calendar-manage"));

                enriched.Add(new {
                    id = full["id"]?.ToString() ?? ids[i],
                    name,
                    shortCode    = full["shortCode"]?.ToString() ?? "",
                    description  = full["description"]?.ToString() ?? "",
                    iconUrl      = full["iconUrl"]?.ToString() ?? "",
                    bannerUrl    = full["bannerUrl"]?.ToString() ?? "",
                    memberCount  = full["memberCount"]?.Value<int>() ?? 0,
                    privacy      = full["privacy"]?.ToString() ?? "",
                    joinState    = full["joinState"]?.ToString() ?? "",
                    canCreateInstance = canCreate,
                    canPost, canEvent,
                });
            }
            if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyGroups, enriched);
            _core.SendToJS("log", new { msg = $"[GROUPS] {enriched.Count} loaded", color = "sec" });
            _core.SendToJS("vrcMyGroups", enriched);
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"Groups load error: {ex.Message}", color = "err" });
        }
    }

    // Message handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcSearchGroups":
            {
                var gQ = msg["query"]?.ToString() ?? "";
                var gOff = msg["offset"]?.Value<int>() ?? 0;
                _ = Task.Run(async () =>
                {
                    var res = await _core.VrcApi.SearchGroupsAsync(gQ, 20, gOff);
                    var list = res.Cast<JObject>().Select(g => new {
                        id = g["id"]?.ToString() ?? "", name = g["name"]?.ToString() ?? "",
                        shortCode = g["shortCode"]?.ToString() ?? "", description = g["description"]?.ToString() ?? "",
                        iconUrl = g["iconUrl"]?.ToString() ?? "", bannerUrl = g["bannerUrl"]?.ToString() ?? "",
                        memberCount = g["memberCount"]?.Value<int>() ?? 0, privacy = g["privacy"]?.ToString() ?? "",
                    }).ToList();
                    _core.SendToJS("vrcSearchResults", new { type = "groups", results = list, offset = gOff, hasMore = list.Count >= 20 });
                });
                break;
            }

            case "vrcGetMyGroups":
            {
                if (_core.Settings.FfcEnabled)
                {
                    var cachedGrps = _core.Cache.LoadRaw(CacheHandler.KeyGroups);
                    if (cachedGrps != null) _core.SendToJS("vrcMyGroups", cachedGrps);
                }
                _ = Task.Run(FetchAndCacheAsync);
                break;
            }

            case "vrcGetGroup":
            {
                var ggId = msg["groupId"]?.ToString();
                if (!string.IsNullOrEmpty(ggId))
                {
                    _ = Task.Run(async () =>
                    {
                        var g = await _core.VrcApi.GetGroupAsync(ggId);
                        if (g != null)
                        {
                            bool isMember = g["myMember"] != null && g["myMember"]!.Type != JTokenType.Null;
                            // Fetch additional data in parallel
                            var postsTask = _core.VrcApi.GetGroupPostsAsync(ggId, publicOnly: !isMember);
                            var instancesTask = _core.VrcApi.GetGroupInstancesAsync(ggId);
                            var membersTask = _core.VrcApi.GetGroupMembersAsync(ggId);
                            var eventsTask = _core.VrcApi.GetGroupEventsAsync(ggId);

                            await Task.WhenAll(postsTask, instancesTask, membersTask, eventsTask);

                            var posts = postsTask.Result;
                            var instances = instancesTask.Result;
                            var members = membersTask.Result;
                            var events = eventsTask.Result;

                            // Fetch gallery images for all galleries
                            var galleries = g["galleries"] as JArray ?? new JArray();
                            var galleryImages = new List<object>();
                            foreach (var gal in galleries)
                            {
                                var galId = gal["id"]?.ToString();
                                var galName = gal["name"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(galId))
                                {
                                    var imgs = await _core.VrcApi.GetGroupGalleryImagesAsync(ggId, galId);
                                    foreach (var img in imgs)
                                    {
                                        galleryImages.Add(new {
                                            imageUrl = img["imageUrl"]?.ToString() ?? "",
                                            galleryName = galName,
                                            createdAt = img["createdAt"]?.ToString() ?? "",
                                        });
                                    }
                                }
                            }

                            var myMember = g["myMember"] as JObject;
                            var myPerms = myMember?["permissions"] as JArray ?? new JArray();
                            var canPost  = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-announcement-manage");
                            var canEvent = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-calendar-manage");
                            var canEdit  = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-data-manage");
                            var canKick        = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-members-remove");
                            var canBan         = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-bans-manage");
                            var canManageRoles = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-roles-manage");
                            var canAssignRoles = myPerms.Any(p => p.ToString() == "*" || p.ToString() == "group-roles-manage" || p.ToString() == "group-roles-assign");

                            _core.SendToJS("vrcGroupDetail", new {
                                id = g["id"]?.ToString() ?? "", name = g["name"]?.ToString() ?? "",
                                shortCode = g["shortCode"]?.ToString() ?? "", description = g["description"]?.ToString() ?? "",
                                iconUrl = g["iconUrl"]?.ToString() ?? "", bannerUrl = g["bannerUrl"]?.ToString() ?? "",
                                memberCount = g["memberCount"]?.Value<int>() ?? 0, privacy = g["privacy"]?.ToString() ?? "",
                                joinState = g["joinState"]?.ToString() ?? "",
                                rules = g["rules"]?.ToString() ?? "",
                                languages = (g["languages"] as JArray)?.Select(x => x.ToString()).ToArray() ?? Array.Empty<string>(),
                                links     = (g["links"]     as JArray)?.Select(x => x.ToString()).ToArray() ?? Array.Empty<string>(),
                                isJoined = g["myMember"] != null && g["myMember"].Type != JTokenType.Null,
                                canPost, canEvent, canEdit, canKick, canBan, canManageRoles, canAssignRoles,
                                roles = (g["roles"] as JArray ?? new JArray()).Select(r => {
                                    var rPerms = (r["permissions"] as JArray)?.Select(p => p.ToString()).ToArray() ?? Array.Empty<string>();
                                    _core.SendToJS("log", new { msg = $"[ROLE] \"{r["name"]}\" perms: [{string.Join(", ", rPerms)}]", color = "sec" });
                                    return new {
                                        id              = r["id"]?.ToString() ?? "",
                                        name            = r["name"]?.ToString() ?? "",
                                        description     = r["description"]?.ToString() ?? "",
                                        permissions     = rPerms,
                                        isAddedOnJoin   = r["isAddedOnJoin"]?.Value<bool>() ?? false,
                                        isSelfAssignable  = r["isSelfAssignable"]?.Value<bool>() ?? false,
                                        requiresTwoFactor = r["requiresTwoFactor"]?.Value<bool>() ?? false,
                                        isManagementRole  = r["isManagementRole"]?.Value<bool>() ?? false,
                                    };
                                }),
                                posts = posts.Select(p => new {
                                    id = p["id"]?.ToString() ?? "",
                                    title = p["title"]?.ToString() ?? "",
                                    text = p["text"]?.ToString() ?? "",
                                    imageUrl = p["imageUrl"]?.ToString() ?? "",
                                    createdAt = p["createdAt"]?.ToString() ?? "",
                                    authorId = p["authorId"]?.ToString() ?? "",
                                    visibility = p["visibility"]?.ToString() ?? "",
                                }),
                                groupEvents = events.Select(e => new {
                                    id = e["id"]?.ToString() ?? "",
                                    ownerId = e["ownerId"]?.ToString() ?? "",
                                    title = e["title"]?.ToString() ?? "",
                                    description = e["description"]?.ToString() ?? "",
                                    startsAt = e["startsAt"]?.ToString() ?? "",
                                    endsAt = e["endsAt"]?.ToString() ?? "",
                                    imageUrl = e["imageUrl"]?.ToString() ?? "",
                                    accessType = e["accessType"]?.ToString() ?? "",
                                }),
                                groupInstances = instances.Select(i => new {
                                    instanceId = i["instanceId"]?.ToString() ?? "",
                                    location = i["location"]?.ToString() ?? "",
                                    worldName = i["world"]?["name"]?.ToString() ?? "",
                                    worldThumb = i["world"]?["thumbnailImageUrl"]?.ToString() ?? i["world"]?["imageUrl"]?.ToString() ?? "",
                                    userCount = i["userCount"]?.Value<int>() ?? i["n_users"]?.Value<int>() ?? 0,
                                    capacity = i["world"]?["capacity"]?.Value<int>() ?? 0,
                                }),
                                galleryImages,
                                groupMembers = members.Select(m => new {
                                    id = m["userId"]?.ToString() ?? "",
                                    displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                                    image = m["user"] is JObject gmu
                                        ? (VRChatApiService.GetUserImage(gmu) is var gi && gi.Length > 0 ? gi : gmu["thumbnailUrl"]?.ToString() ?? "")
                                        : "",
                                    status = m["user"]?["status"]?.ToString() ?? "",
                                    statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                                    roleIds = (m["roleIds"] as JArray)?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>(),
                                    joinedAt = m["joinedAt"]?.ToString() ?? "",
                                }),
                            });
                        }
                        else
                        {
                            _core.SendToJS("vrcGroupDetailError", new { error = $"Could not load group {ggId}" });
                        }
                    });
                }
                break;
            }

            case "vrcJoinGroup":
            {
                var jgId = msg["groupId"]?.ToString();
                if (!string.IsNullOrEmpty(jgId))
                {
                    _ = Task.Run(async () => {
                        var ok = await _core.VrcApi.JoinGroupAsync(jgId);
                        _core.SendToJS("vrcActionResult", new { action = "joinGroup", success = ok,
                            message = ok ? "Group join request sent!" : "Failed to join group" });
                    });
                }
                break;
            }

            case "vrcGetGroupMembers":
            {
                var gmId = msg["groupId"]?.ToString();
                var gmOffset = msg["offset"]?.Value<int>() ?? 0;
                if (!string.IsNullOrEmpty(gmId))
                {
                    _ = Task.Run(async () => {
                        var members = await _core.VrcApi.GetGroupMembersAsync(gmId, 50, gmOffset);
                        var list = members.Select(m => new {
                            id = m["userId"]?.ToString() ?? "",
                            displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                            image = m["user"] is JObject gmu2
                                ? (VRChatApiService.GetUserImage(gmu2) is var gi2 && gi2.Length > 0 ? gi2 : gmu2["thumbnailUrl"]?.ToString() ?? "")
                                : "",
                            status = m["user"]?["status"]?.ToString() ?? "",
                            statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                            roleIds = (m["roleIds"] as JArray)?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>(),
                            joinedAt = m["joinedAt"]?.ToString() ?? "",
                        }).ToList();
                        _core.SendToJS("vrcGroupMembersPage", new {
                            groupId = gmId, offset = gmOffset, members = list,
                            hasMore = members.Count >= 50,
                        });
                    });
                }
                break;
            }

            case "vrcSearchGroupMembers":
            {
                var sgmId = msg["groupId"]?.ToString() ?? "";
                var sgmQuery = msg["query"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(sgmId) && !string.IsNullOrEmpty(sgmQuery))
                {
                    _ = Task.Run(async () => {
                        var members = await _core.VrcApi.SearchGroupMembersAsync(sgmId, sgmQuery);
                        var list = members.Select(m => new {
                            id = m["userId"]?.ToString() ?? "",
                            displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                            image = m["user"] is JObject sgmu
                                ? (VRChatApiService.GetUserImage(sgmu) is var sgi && sgi.Length > 0 ? sgi : sgmu["thumbnailUrl"]?.ToString() ?? "")
                                : "",
                            status = m["user"]?["status"]?.ToString() ?? "",
                            statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                            roleIds = (m["roleIds"] as JArray)?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>(),
                            joinedAt = m["joinedAt"]?.ToString() ?? "",
                        }).ToList();
                        _core.SendToJS("vrcGroupSearchResults", new {
                            groupId = sgmId, query = sgmQuery, members = list,
                        });
                    });
                }
                break;
            }

            case "vrcGetGroupRoleMembers":
            {
                var grmGroupId = msg["groupId"]?.ToString() ?? "";
                var grmRoleId  = msg["roleId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(grmGroupId) && !string.IsNullOrEmpty(grmRoleId))
                    _ = Task.Run(async () => {
                        var members = await _core.VrcApi.GetGroupRoleMembersAsync(grmGroupId, grmRoleId);
                        var list = members.Select(m => new {
                            id = m["userId"]?.ToString() ?? "",
                            displayName = m["user"]?["displayName"]?.ToString() ?? m["displayName"]?.ToString() ?? "",
                            image = m["user"] is JObject ru
                                ? (VRChatApiService.GetUserImage(ru) is var ri && ri.Length > 0 ? ri : ru["thumbnailUrl"]?.ToString() ?? "")
                                : "",
                            status = m["user"]?["status"]?.ToString() ?? "",
                            statusDescription = m["user"]?["statusDescription"]?.ToString() ?? "",
                        }).ToList();
                        _core.SendToJS("vrcGroupRoleMembers", new { groupId = grmGroupId, roleId = grmRoleId, members = list });
                    });
                break;
            }

            case "vrcLeaveGroup":
            {
                var lgId = msg["groupId"]?.ToString();
                if (!string.IsNullOrEmpty(lgId))
                {
                    _ = Task.Run(async () => {
                        var ok = await _core.VrcApi.LeaveGroupAsync(lgId);
                        _core.SendToJS("vrcActionResult", new { action = "leaveGroup", success = ok,
                            message = ok ? "Left group" : "Failed to leave group" });
                    });
                }
                break;
            }

            case "vrcCreateGroupPost":
            {
                var cpGroupId = msg["groupId"]?.ToString() ?? "";
                var cpTitle = msg["title"]?.ToString() ?? "";
                var cpText = msg["text"]?.ToString() ?? "";
                var cpVisibility = msg["visibility"]?.ToString() ?? "group";
                var cpNotify = msg["sendNotification"]?.Value<bool>() ?? false;
                var cpImageBase64 = msg["imageBase64"]?.ToString();
                var cpImageFileId = msg["imageFileId"]?.ToString();
                if (!string.IsNullOrEmpty(cpGroupId) && !string.IsNullOrEmpty(cpTitle))
                {
                    _ = Task.Run(async () =>
                    {
                        string? imageId = null;
                        if (!string.IsNullOrEmpty(cpImageFileId))
                        {
                            imageId = cpImageFileId;
                            _core.SendToJS("log", new { msg = $"[GroupPost] Using library image: {imageId}", color = "sec" });
                        }
                        else if (!string.IsNullOrEmpty(cpImageBase64))
                        {
                            try
                            {
                                var b64 = cpImageBase64;
                                string imgMime = "image/png";
                                string imgExt = ".png";
                                if (b64.StartsWith("data:"))
                                {
                                    var semi = b64.IndexOf(';');
                                    if (semi > 5) imgMime = b64[5..semi];
                                    imgExt = imgMime switch
                                    {
                                        "image/jpeg" => ".jpg",
                                        "image/gif"  => ".gif",
                                        "image/webp" => ".webp",
                                        _            => ".png"
                                    };
                                }
                                var commaIdx = b64.IndexOf(',');
                                if (commaIdx >= 0) b64 = b64[(commaIdx + 1)..];
                                var imgBytes = Convert.FromBase64String(b64);
                                _core.SendToJS("log", new { msg = $"[GroupPost] Uploading image {imgMime} {imgBytes.Length / 1024} KB", color = "sec" });
                                imageId = await _core.VrcApi.UploadImageAsync(imgBytes, imgMime, imgExt);
                                if (imageId == null)
                                    _core.SendToJS("log", new { msg = "[GroupPost] Image upload failed, posting without image", color = "warn" });
                                else
                                    _core.SendToJS("log", new { msg = $"[GroupPost] Image uploaded: {imageId}", color = "sec" });
                            }
                            catch (Exception ex)
                            {
                                _core.SendToJS("log", new { msg = $"[GroupPost] Image parse error: {ex.Message}", color = "err" });
                            }
                        }
                        var ok = await _core.VrcApi.CreateGroupPostAsync(cpGroupId, cpTitle, cpText, cpVisibility, cpNotify, imageId);
                        _core.SendToJS("vrcActionResult", new
                        {
                            action = "createGroupPost",
                            success = ok,
                            message = ok ? "Post created!" : "Failed to create post"
                        });
                    });
                }
                break;
            }

            case "vrcDeleteGroupPost":
            {
                var dgpGroupId = msg["groupId"]?.ToString() ?? "";
                var dgpPostId  = msg["postId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(dgpGroupId) && !string.IsNullOrEmpty(dgpPostId))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.DeleteGroupPostAsync(dgpGroupId, dgpPostId);
                        _core.SendToJS("vrcActionResult", new { action = "deleteGroupPost", success = ok, postId = dgpPostId });
                    });
                }
                break;
            }

            case "vrcDeleteGroupEvent":
            {
                var dgeGroupId  = msg["groupId"]?.ToString() ?? "";
                var dgeEventId  = msg["eventId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(dgeGroupId) && !string.IsNullOrEmpty(dgeEventId))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.DeleteGroupEventAsync(dgeGroupId, dgeEventId);
                        _core.SendToJS("vrcActionResult", new { action = "deleteGroupEvent", success = ok, eventId = dgeEventId });
                    });
                }
                break;
            }

            case "vrcUpdateGroup":
            {
                var ugGroupId   = msg["groupId"]?.ToString() ?? "";
                var ugDesc      = msg["description"] != null ? msg["description"]!.ToString() : (string?)null;
                var ugRules     = msg["rules"]       != null ? msg["rules"]!.ToString()       : (string?)null;
                var ugLanguages = msg["languages"]?.ToObject<List<string>>();
                var ugLinks     = msg["links"]?.ToObject<List<string>>();
                var ugIconId    = msg["iconId"]    != null ? msg["iconId"]!.ToString()    : (string?)null;
                var ugBannerId  = msg["bannerId"]  != null ? msg["bannerId"]!.ToString()  : (string?)null;
                var ugJoinState = msg["joinState"] != null ? msg["joinState"]!.ToString() : (string?)null;
                if (!string.IsNullOrEmpty(ugGroupId))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.UpdateGroupAsync(ugGroupId, ugDesc, ugRules, ugLanguages, ugLinks, ugIconId, ugBannerId, ugJoinState);
                        _core.SendToJS("vrcGroupUpdated", new {
                            success = ok, groupId = ugGroupId,
                            description = ugDesc, rules = ugRules,
                            languages = ugLanguages, links = ugLinks,
                            iconId = ugIconId, bannerId = ugBannerId,
                            joinState = ugJoinState
                        });
                    });
                }
                break;
            }

            case "vrcKickGroupMember":
            {
                var kmGroupId = msg["groupId"]?.ToString() ?? "";
                var kmUserId  = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(kmGroupId) && !string.IsNullOrEmpty(kmUserId))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.KickGroupMemberAsync(kmGroupId, kmUserId);
                        _core.SendToJS("vrcActionResult", new { action = "kickGroupMember", success = ok, message = ok ? "Member kicked." : "Kick failed." });
                    });
                break;
            }

            case "vrcBanGroupMember":
            {
                var bmGroupId = msg["groupId"]?.ToString() ?? "";
                var bmUserId  = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(bmGroupId) && !string.IsNullOrEmpty(bmUserId))
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.BanGroupMemberAsync(bmGroupId, bmUserId);
                        _core.SendToJS("vrcActionResult", new { action = "banGroupMember", success = ok, message = ok ? "Member banned." : "Ban failed." });
                    });
                break;
            }

            case "vrcGetGroupBans":
            {
                var gbId = msg["groupId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(gbId))
                    _ = Task.Run(async () => {
                        var bans = await _core.VrcApi.GetGroupBansAsync(gbId);
                        var list = bans.Select(b => new {
                            id          = b["userId"]?.ToString() ?? "",
                            displayName = b["user"]?["displayName"]?.ToString() ?? b["displayName"]?.ToString() ?? "",
                            image       = b["user"] is JObject gu ? (VRChatApiService.GetUserImage(gu) is var gi && gi.Length > 0 ? gi : gu["thumbnailUrl"]?.ToString() ?? "") : "",
                            bannedAt    = b["bannedAt"]?.ToString() ?? b["createdAt"]?.ToString() ?? "",
                        }).ToList();
                        _core.SendToJS("vrcGroupBans", new { groupId = gbId, bans = list });
                    });
                break;
            }

            case "vrcUnbanGroupMember":
            {
                var ubGroupId = msg["groupId"]?.ToString() ?? "";
                var ubUserId  = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(ubGroupId) && !string.IsNullOrEmpty(ubUserId))
                    _ = Task.Run(async () => {
                        var ok = await _core.VrcApi.UnbanGroupMemberAsync(ubGroupId, ubUserId);
                        _core.SendToJS("vrcActionResult", new { action = "unbanGroupMember", success = ok, userId = ubUserId, message = ok ? "Member unbanned." : "Unban failed." });
                    });
                break;
            }

            case "vrcCreateGroupRole":
            {
                var crGroupId = msg["groupId"]?.ToString() ?? "";
                var crName    = msg["name"]?.ToString() ?? "";
                var crDesc    = msg["description"]?.ToString() ?? "";
                var crPerms   = msg["permissions"]?.ToObject<List<string>>() ?? new List<string>();
                var crJoin    = msg["isAddedOnJoin"]?.Value<bool>() ?? false;
                var crSelf    = msg["isSelfAssignable"]?.Value<bool>() ?? false;
                var crTfa     = msg["requiresTwoFactor"]?.Value<bool>() ?? false;
                if (!string.IsNullOrEmpty(crGroupId) && !string.IsNullOrEmpty(crName))
                    _ = Task.Run(async () => {
                        var role = await _core.VrcApi.CreateGroupRoleAsync(crGroupId, crName, crDesc, crPerms, crJoin, crSelf, crTfa);
                        var ok = role != null;
                        object? roleData = ok ? (object)new {
                            id              = role!["id"]?.ToString() ?? "",
                            name            = role["name"]?.ToString() ?? "",
                            description     = role["description"]?.ToString() ?? "",
                            permissions     = (role["permissions"] as JArray)?.Select(p => p.ToString()).ToArray() ?? Array.Empty<string>(),
                            isAddedOnJoin   = role["isAddedOnJoin"]?.Value<bool>() ?? false,
                            isSelfAssignable  = role["isSelfAssignable"]?.Value<bool>() ?? false,
                            requiresTwoFactor = role["requiresTwoFactor"]?.Value<bool>() ?? false,
                            isManagementRole  = role["isManagementRole"]?.Value<bool>() ?? false,
                        } : null;
                        _core.SendToJS("vrcGroupRoleResult", new { action = "create", success = ok, groupId = crGroupId, role = roleData });
                    });
                break;
            }

            case "vrcUpdateGroupRole":
            {
                var urGroupId = msg["groupId"]?.ToString() ?? "";
                var urRoleId  = msg["roleId"]?.ToString() ?? "";
                var urName    = msg["name"]        != null ? msg["name"]!.ToString()        : (string?)null;
                var urDesc    = msg["description"] != null ? msg["description"]!.ToString() : (string?)null;
                var urPerms   = msg["permissions"]?.ToObject<List<string>>();
                var urJoin    = msg["isAddedOnJoin"]    != null ? (bool?)msg["isAddedOnJoin"]!.Value<bool>()    : null;
                var urSelf    = msg["isSelfAssignable"] != null ? (bool?)msg["isSelfAssignable"]!.Value<bool>() : null;
                var urTfa     = msg["requiresTwoFactor"]!= null ? (bool?)msg["requiresTwoFactor"]!.Value<bool>(): null;
                if (!string.IsNullOrEmpty(urGroupId) && !string.IsNullOrEmpty(urRoleId))
                    _ = Task.Run(async () => {
                        var ok = await _core.VrcApi.UpdateGroupRoleAsync(urGroupId, urRoleId, urName, urDesc, urPerms, urJoin, urSelf, urTfa);
                        _core.SendToJS("vrcGroupRoleResult", new { action = "update", success = ok, groupId = urGroupId, roleId = urRoleId });
                    });
                break;
            }

            case "vrcDeleteGroupRole":
            {
                var drGroupId = msg["groupId"]?.ToString() ?? "";
                var drRoleId  = msg["roleId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(drGroupId) && !string.IsNullOrEmpty(drRoleId))
                    _ = Task.Run(async () => {
                        var ok = await _core.VrcApi.DeleteGroupRoleAsync(drGroupId, drRoleId);
                        _core.SendToJS("vrcGroupRoleResult", new { action = "delete", success = ok, groupId = drGroupId, roleId = drRoleId });
                    });
                break;
            }

            case "vrcAddGroupMemberRole":
            {
                var amrGroupId = msg["groupId"]?.ToString() ?? "";
                var amrUserId  = msg["userId"]?.ToString() ?? "";
                var amrRoleId  = msg["roleId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(amrGroupId) && !string.IsNullOrEmpty(amrUserId) && !string.IsNullOrEmpty(amrRoleId))
                    _ = Task.Run(async () => {
                        var ok = await _core.VrcApi.AddGroupMemberRoleAsync(amrGroupId, amrUserId, amrRoleId);
                        _core.SendToJS("vrcActionResult", new { action = "addGroupMemberRole", success = ok, userId = amrUserId, roleId = amrRoleId, message = ok ? "Role assigned." : "Failed to assign role." });
                    });
                break;
            }

            case "vrcRemoveGroupMemberRole":
            {
                var rmrGroupId = msg["groupId"]?.ToString() ?? "";
                var rmrUserId  = msg["userId"]?.ToString() ?? "";
                var rmrRoleId  = msg["roleId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(rmrGroupId) && !string.IsNullOrEmpty(rmrUserId) && !string.IsNullOrEmpty(rmrRoleId))
                    _ = Task.Run(async () => {
                        var ok = await _core.VrcApi.RemoveGroupMemberRoleAsync(rmrGroupId, rmrUserId, rmrRoleId);
                        _core.SendToJS("vrcActionResult", new { action = "removeGroupMemberRole", success = ok, userId = rmrUserId, roleId = rmrRoleId, message = ok ? "Role removed." : "Failed to remove role." });
                    });
                break;
            }

            case "vrcCreateGroupEvent":
            {
                var ceGroupId   = msg["groupId"]?.ToString() ?? "";
                var ceTitle     = msg["title"]?.ToString() ?? "";
                var ceDesc      = msg["description"]?.ToString() ?? "";
                var ceStartsAt  = msg["startsAt"]?.ToString() ?? "";
                var ceEndsAt    = msg["endsAt"]?.ToString() ?? "";
                var ceCategory  = msg["category"]?.ToString() ?? "other";
                var ceAccess    = msg["accessType"]?.ToString() ?? "group";
                var ceNotify    = msg["sendCreationNotification"]?.Value<bool>() ?? false;
                var ceImageB64  = msg["imageBase64"]?.ToString();
                var ceImageFileId = msg["imageFileId"]?.ToString();
                if (!string.IsNullOrEmpty(ceGroupId) && !string.IsNullOrEmpty(ceTitle) && !string.IsNullOrEmpty(ceStartsAt))
                {
                    _ = Task.Run(async () =>
                    {
                        string? imageId = null;
                        if (!string.IsNullOrEmpty(ceImageFileId))
                        {
                            imageId = ceImageFileId;
                        }
                        else if (!string.IsNullOrEmpty(ceImageB64))
                        {
                            try
                            {
                                var b64 = ceImageB64;
                                string imgMime = "image/png", imgExt = ".png";
                                if (b64.StartsWith("data:"))
                                {
                                    var semi = b64.IndexOf(';');
                                    if (semi > 5) imgMime = b64[5..semi];
                                    imgExt = imgMime switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png" };
                                }
                                var commaIdx = b64.IndexOf(',');
                                if (commaIdx >= 0) b64 = b64[(commaIdx + 1)..];
                                var imgBytes = Convert.FromBase64String(b64);
                                _core.SendToJS("log", new { msg = $"[GroupEvent] Uploading image {imgMime} {imgBytes.Length / 1024} KB", color = "sec" });
                                imageId = await _core.VrcApi.UploadImageAsync(imgBytes, imgMime, imgExt);
                                if (imageId == null)
                                    _core.SendToJS("log", new { msg = "[GroupEvent] Image upload failed, creating event without image", color = "warn" });
                            }
                            catch (Exception ex) { _core.SendToJS("log", new { msg = $"[GroupEvent] Image error: {ex.Message}", color = "err" }); }
                        }
                        var result = await _core.VrcApi.CreateGroupEventAsync(ceGroupId, ceTitle, ceDesc, ceStartsAt, ceEndsAt, ceCategory, ceAccess, ceNotify, imageId);
                        var ok = result != null;
                        _core.SendToJS("vrcActionResult", new
                        {
                            action = "createGroupEvent",
                            success = ok,
                            message = ok ? "Event created!" : "Failed to create event"
                        });
                    });
                }
                break;
            }

            case "vrcGetMutualsForNetwork":
            {
                var mnUid = msg["userId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(mnUid))
                {
                    _ = Task.Run(async () =>
                    {
                        var (arr, optedOut) = await _core.VrcApi.GetUserMutualsAsync(mnUid);
                        var ids = optedOut ? Array.Empty<string>()
                                           : arr.Select(m => m["id"]?.ToString() ?? "").Where(s => s != "").ToArray();
                        _core.SendToJS("vrcMutualsForNetwork", new { userId = mnUid, mutualIds = ids, optedOut });
                    });
                }
                break;
            }

            case "vrcSaveMutualCache":
            {
                var mcJson = msg["cache"]?.ToString() ?? "{}";
                _ = Task.Run(() =>
                {
                    try
                    {
                        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext");
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "mutual_cache.json"), mcJson, System.Text.Encoding.UTF8);
                    }
                    catch { /* non-critical */ }
                });
                break;
            }

            case "vrcLoadMutualCache":
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "mutual_cache.json");
                        var json = File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : "{}";
                        _core.SendToJS("vrcMutualCacheLoaded", new { json });
                    }
                    catch
                    {
                        _core.SendToJS("vrcMutualCacheLoaded", new { json = "{}" });
                    }
                });
                break;
            }

            case "vrcClearMutualCache":
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "mutual_cache.json");
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { /* non-critical */ }
                });
                break;
            }

            case "vrcCreateGroupInstance":
            {
                var cgiWorldId = msg["worldId"]?.ToString() ?? "";
                var cgiGroupId = msg["groupId"]?.ToString() ?? "";
                var cgiAccessType = msg["groupAccessType"]?.ToString() ?? "members";
                var cgiRegion = msg["region"]?.ToString() ?? "eu";
                if (!string.IsNullOrEmpty(cgiWorldId) && !string.IsNullOrEmpty(cgiGroupId))
                {
                    _ = Task.Run(async () =>
                    {
                        var location = await _core.VrcApi.CreateGroupInstanceAsync(cgiWorldId, cgiGroupId, cgiAccessType, cgiRegion);
                        if (!string.IsNullOrEmpty(location))
                        {
                            var ok = await _core.VrcApi.InviteSelfAsync(location);
                            if (ok)
                            {
                                _core.Settings.MyInstances.Remove(location);
                                _core.Settings.MyInstances.Insert(0, location);
                                _core.Settings.Save();
                            }
                            _core.SendToJS("vrcActionResult", new
                            {
                                action = "createInstance",
                                success = ok,
                                message = ok ? "Group instance created! Self-invite sent." : "Instance created but invite failed.",
                                location
                            });
                        }
                        else
                        {
                            _core.SendToJS("vrcActionResult", new
                            {
                                action = "createInstance",
                                success = false,
                                message = "Failed to create group instance."
                            });
                        }
                    });
                }
                break;
            }
        }

        await Task.CompletedTask;
    }
}
