using System.Text.Json.Nodes;
using LocalIdeBridge.Storage;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class AgentBoardTests
{
    [Fact]
    public async Task MessagesSurviveBoardReopenAndDeduplicateRetries()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (agent, secret) = board.Join(room, "Lead", "coordinator");

        var first = board.Post(room, agent.Id, secret, "message", "Check the download engine", "message-1");
        var retry = board.Post(room, agent.Id, secret, "message", "Check the download engine", "message-1");
        Assert.Equal(first.Sequence, retry.Sequence);

        var reopened = new AgentBoardStore(sb.Paths, sb.Host.Credentials);
        reopened.Initialize();
        var sameRoom = reopened.RequireRoom(room.Id, sb.WorkspaceDir);
        var events = reopened.ReadEvents(sameRoom, 0, 10).Events;
        Assert.Single(events);
        Assert.Equal("Check the download engine", events[0].Body);

        Assert.Throws<InvalidOperationException>(() =>
            board.Post(room, agent.Id, secret, "message", "Different message", "message-1"));
    }

    [Fact]
    public async Task PrivateMessagesAreReadableToSenderAndRecipientOnly()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (sender, senderKey) = board.Join(room, "Engine", "worker");
        var (recipient, _) = board.Join(room, "UI", "worker");
        var (other, _) = board.Join(room, "Reviewer", "reviewer");

        var message = board.Post(room, sender.Id, senderKey, "message",
            "Please check the media API", "dm-1", recipient.Id);

        Assert.Equal(message.Sequence,
            Assert.Single(board.ReadEvents(room, 0, 10, sender.Id).Events).Sequence);
        Assert.Equal(message.Sequence,
            Assert.Single(board.ReadEvents(room, 0, 10, recipient.Id).Events).Sequence);
        Assert.Empty(board.ReadEvents(room, 0, 10, other.Id).Events);
        Assert.Equal(0, board.LatestSequence(room, other.Id));
        Assert.Equal(message.Sequence, board.LatestSequence(room, sender.Id));
    }

    [Fact]
    public async Task AcknowledgmentsCannotSkipInvisibleEvents()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (sender, senderKey) = board.Join(room, "Engine", "worker");
        var (recipient, recipientKey) = board.Join(room, "UI", "worker");
        var (other, otherKey) = board.Join(room, "Reviewer", "reviewer");

        var message = board.Post(room, sender.Id, senderKey, "message", "private", "ack-1", recipient.Id);
        Assert.Throws<ArgumentException>(() => board.Acknowledge(room, other.Id, otherKey, message.Sequence));
        board.Acknowledge(room, recipient.Id, recipientKey, message.Sequence);
        board.Acknowledge(room, recipient.Id, recipientKey, message.Sequence);
        Assert.Equal(message.Sequence, board.GetAcknowledged(room, recipient.Id));
        Assert.Equal(0, board.GetAcknowledged(room, other.Id));
    }

    [Fact]
    public async Task PinsRequireExpectedRevisionAndCanBeRemoved()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (agent, secret) = board.Join(room, "Lead", "coordinator");

        var pin = board.SavePin(room, agent.Id, secret, "branch", "Branch", "Work on main", null);
        Assert.Equal(1, pin.Revision);
        Assert.Throws<InvalidOperationException>(() =>
            board.SavePin(room, agent.Id, secret, "branch", "Wrong", "Old revision", null));
        Assert.Throws<InvalidOperationException>(() =>
            board.SavePin(room, agent.Id, secret, "new-pin", "Wrong", "Should not create", 8));

        var next = board.SavePin(room, agent.Id, secret, "branch", "Branch", "main only", pin.Revision);
        Assert.Equal(2, next.Revision);
        Assert.Throws<InvalidOperationException>(() => board.DeletePin(room, agent.Id, secret, "branch", 1));
        board.DeletePin(room, agent.Id, secret, "branch", next.Revision);
        Assert.Empty(board.ListPins(room));
    }

    [Fact]
    public async Task TaskCreationRetriesDoNotCreateDuplicateTasks()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (lead, key) = board.Join(room, "Lead", "coordinator");

        var task = board.CreateTask(room, lead.Id, key, "Improve media discovery",
            "No false positives", "src", "task-request-1");
        var retry = board.CreateTask(room, lead.Id, key, "Improve media discovery",
            "No false positives", "src", "task-request-1");
        Assert.Equal(task.Id, retry.Id);
        Assert.Single(board.ListTasks(room));
        Assert.Throws<InvalidOperationException>(() =>
            board.CreateTask(room, lead.Id, key, "Different title",
                "No false positives", "src", "task-request-1"));
    }

    [Fact]
    public async Task OnlyOneWorkerCanClaimATask()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (a, aKey) = board.Join(room, "Engine", "worker");
        var (b, bKey) = board.Join(room, "Extension", "worker");
        var task = board.CreateTask(room, a.Id, aKey, "Resolver", "Handle test URLs", "src",
            "task-collision-1");

        var claimed = board.ChangeTask(room, a.Id, aKey, task.Id, "claim", task.Revision);
        Assert.Equal(a.Id, claimed.OwnerId);
        Assert.Throws<InvalidOperationException>(() =>
            board.ChangeTask(room, b.Id, bKey, task.Id, "claim", task.Revision));
        Assert.Throws<UnauthorizedAccessException>(() =>
            board.RequireActiveClaim(room, b.Id, bKey, task.Id));
        Assert.Equal(a.Id, board.RequireActiveClaim(room, a.Id, aKey, task.Id).OwnerId);
    }

    [Fact]
    public async Task RejoiningInvalidatesOldCredentialAndRequiresExplicitLeaseAdoption()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (before, oldKey) = board.Join(room, "Engine", "worker");
        var task = board.CreateTask(room, before.Id, oldKey, "HTTP engine",
            "Resume without overwriting old work", null, "task-resume-1");
        var claimed = board.ChangeTask(room, before.Id, oldKey, task.Id, "claim", task.Revision);

        var (after, newKey) = board.Join(room, "Engine", "worker", before.Id, oldKey);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Generation + 1, after.Generation);
        Assert.Throws<UnauthorizedAccessException>(() =>
            board.Authenticate(room, before.Id, oldKey));
        Assert.Throws<UnauthorizedAccessException>(() =>
            board.RequireActiveClaim(room, after.Id, newKey, task.Id));

        var adopted = board.ChangeTask(room, after.Id, newKey, task.Id, "adopt",
            claimed.Revision, evidence: "Checked diffs and running jobs");
        Assert.Equal(after.Generation, adopted.OwnerGeneration);
        Assert.Equal(after.Id, board.RequireActiveClaim(room, after.Id, newKey, task.Id).OwnerId);
    }

    [Fact]
    public async Task WaitingAgentWakesWhenAVisibleMessageArrives()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (sender, senderKey) = board.Join(room, "Engine", "worker");
        var (recipient, _) = board.Join(room, "UI", "worker");

        var wait = board.WaitForEventsAsync(room, 0, 2000, recipient.Id, CancellationToken.None);
        board.Post(room, sender.Id, senderKey, "message", "Done", "wake-1", recipient.Id);
        Assert.True(await wait);
        Assert.Equal(1, board.ReadEvents(room, 0, 10, recipient.Id).Events.Count);
    }

    [Fact]
    public async Task BoardWorkKeepsAnAgentInsideItsClaimedAreaOnMain()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (worker, key) = board.Join(room, "Engine", "worker");
        var task = board.CreateTask(room, worker.Id, key, "Work on engine",
            "Only edit src/engine", "src/engine", "task-guard-1");
        board.ChangeTask(room, worker.Id, key, task.Id, "claim", task.Revision);

        // Only a disposable fixture. board_work refuses non-main checkout paths.
        Directory.CreateDirectory(Path.Combine(sb.WorkspaceDir, ".git"));
        File.WriteAllText(Path.Combine(sb.WorkspaceDir, ".git", "HEAD"), "ref: refs/heads/main\n");
        Directory.CreateDirectory(Path.Combine(sb.WorkspaceDir, "src", "engine"));

        JsonObject Request(string path) => new()
        {
            ["roomId"] = room.Id,
            ["agentId"] = worker.Id,
            ["credential"] = key,
            ["taskId"] = task.Id,
            ["tool"] = "create_file",
            ["arguments"] = new JsonObject { ["path"] = path, ["content"] = "okay\n" }
        };

        var denied = await sb.Host.Registry.InvokeAsync("board_work", Request("other.cs"));
        Assert.False(denied.Success);
        Assert.Equal("outside_task_area", denied.ErrorCode);
        Assert.False(File.Exists(Path.Combine(sb.WorkspaceDir, "other.cs")));

        var allowed = await sb.Host.Registry.InvokeAsync("board_work", Request("src/engine/okay.cs"));
        Assert.True(allowed.Success, allowed.Summary);
        Assert.True(File.Exists(Path.Combine(sb.WorkspaceDir, "src", "engine", "okay.cs")));

        var events = board.ReadLatestEvents(room);
        Assert.Contains(events, e => e.Kind == "status" && e.Body.Contains("create_file completed"));
    }
    [Fact]
    public async Task OnlyCoordinatorCanInviteAndInvitesAreOneUse()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (lead, leadKey) = board.Join(room, "Lead", "coordinator");
        var (other, otherKey) = board.Join(room, "Reader", "reviewer");

        Assert.Throws<UnauthorizedAccessException>(() =>
            board.CreateInvitation(room, other.Id, otherKey, null, "worker"));

        var invite = board.CreateInvitation(room, lead.Id, leadKey, null, "worker");
        var (worker, workerKey) = board.RedeemInvitation(room, "Engine", invite.Code);
        Assert.Equal("worker", worker.Role);
        Assert.NotEmpty(workerKey);
        Assert.Throws<UnauthorizedAccessException>(() =>
            board.RedeemInvitation(room, "Duplicate", invite.Code));
        Assert.Throws<UnauthorizedAccessException>(() =>
            board.Join(room, "Engine", "coordinator", worker.Id, workerKey));
    }

    [Fact]
    public async Task CoordinatorCanInviteNewChatToTakeOverAnExistingWorker()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (lead, leadKey) = board.Join(room, "Lead", "coordinator");
        var (before, oldKey) = board.Join(room, "Engine", "worker");
        var task = board.CreateTask(room, before.Id, oldKey, "Fix resolver",
            "Resolve a test fixture", "engine", "takeover-request");
        var claimed = board.ChangeTask(room, before.Id, oldKey, task.Id, "claim", task.Revision);

        var invite = board.CreateInvitation(room, lead.Id, leadKey, before.Id, "worker");
        var (after, newKey) = board.RedeemInvitation(room, "Engine Replaced", invite.Code);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Generation + 1, after.Generation);
        Assert.Throws<UnauthorizedAccessException>(() => board.Authenticate(room, before.Id, oldKey));
        Assert.Throws<UnauthorizedAccessException>(() =>
            board.RequireActiveClaim(room, after.Id, newKey, task.Id));

        var adopted = board.ChangeTask(room, after.Id, newKey, task.Id, "adopt",
            claimed.Revision, evidence: "Reviewed current engine diff and running jobs");
        Assert.Equal(after.Generation, adopted.OwnerGeneration);
    }

    [Fact]
    public async Task OverlappingTaskClaimsAcrossRoomsRequireReconciliation()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var engineRoom = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var testRoom = board.GetOrCreateRoom("VESPER Review", sb.WorkspaceDir);
        var (engine, engineKey) = board.Join(engineRoom, "Engine", "worker");
        var (reviewer, reviewerKey) = board.Join(testRoom, "Reviewer", "reviewer");

        var engineTask = board.CreateTask(engineRoom, engine.Id, engineKey, "Engine task",
            "Implement transfer behavior", "src/engine", "area-one");
        var claimed = board.ChangeTask(engineRoom, engine.Id, engineKey, engineTask.Id,
            "claim", engineTask.Revision);
        Assert.Equal("in_progress", claimed.Status);

        var overlap = board.CreateTask(testRoom, reviewer.Id, reviewerKey, "Touch the same code",
            "Conflict", "src/engine/http", "area-two");
        Assert.Throws<InvalidOperationException>(() =>
            board.ChangeTask(testRoom, reviewer.Id, reviewerKey, overlap.Id, "claim", overlap.Revision));

        var independent = board.CreateTask(testRoom, reviewer.Id, reviewerKey, "Review docs",
            "No overlapping edits", "docs", "area-three");
        var independentClaim = board.ChangeTask(testRoom, reviewer.Id, reviewerKey,
            independent.Id, "claim", independent.Revision);
        Assert.Equal("in_progress", independentClaim.Status);

        var newRevision = board.ChangeTask(engineRoom, engine.Id, engineKey, engineTask.Id,
            "release", claimed.Revision);
        Assert.Equal("open", newRevision.Status);
        var afterRelease = board.ChangeTask(testRoom, reviewer.Id, reviewerKey,
            overlap.Id, "claim", overlap.Revision);
        Assert.Equal("in_progress", afterRelease.Status);
    }

    [Fact]
    public async Task LocalOwnerCanPinAndSteerWithoutImpersonatingAnAgent()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var pin = board.SaveSupervisorPin(room, "git", "Branch policy", "Main only");
        Assert.Equal("Main only", pin.Body);
        var eventRecord = board.PostSupervisor(room, "No branch creation.");
        Assert.Equal("owner", eventRecord.Author);
        Assert.Equal("steering", eventRecord.Kind);
        Assert.Contains(board.ReadLatestEvents(room), e => e.Author == "owner" && e.Kind == "decision");
        board.DeleteSupervisorPin(room, "git");
        Assert.Empty(board.ListPins(room));
    }

    [Fact]
    public async Task CoordinatorAndWorkerCanJoinThroughTheActualToolRegistry()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var join = await sb.Host.Registry.InvokeAsync("board_join", new JsonObject
        {
            ["room"] = "VESPER",
            ["name"] = "Lead",
            ["role"] = "coordinator"
        });
        Assert.True(join.Success, join.ErrorMessage);
        var leader = JsonSerializer.SerializeToNode(join.Data, LocalIdeBridge.Core.AppConfig.JsonOptions)!;
        var room = leader["room"]!["id"]!.GetValue<string>();
        var leaderId = leader["agent"]!["id"]!.GetValue<string>();
        var leaderKey = leader["credential"]!.GetValue<string>();

        var invite = sb.Host.Board.CreateInvitation(
            sb.Host.Board.RequireRoom(room, sb.WorkspaceDir), leaderId, leaderKey, null, "worker");
        var workerJoin = await sb.Host.Registry.InvokeAsync("board_join", new JsonObject
        {
            ["room"] = room,
            ["name"] = "Engine",
            ["inviteCode"] = invite.Code
        });
        Assert.True(workerJoin.Success, workerJoin.ErrorMessage);
        var worker = JsonSerializer.SerializeToNode(workerJoin.Data, LocalIdeBridge.Core.AppConfig.JsonOptions)!;
        var workerId = worker["agent"]!["id"]!.GetValue<string>();
        var workerKey = worker["credential"]!.GetValue<string>();

        var task = await sb.Host.Registry.InvokeAsync("board_task", new JsonObject
        {
            ["roomId"] = room, ["agentId"] = leaderId, ["credential"] = leaderKey,
            ["action"] = "create", ["requestId"] = "registry-task-1",
            ["title"] = "Make resolver reliable", ["details"] = "Handle HTTP errors without corrupting files",
            ["workArea"] = "src/resolver"
        });
        Assert.True(task.Success, task.ErrorMessage);

        var sync = await sb.Host.Registry.InvokeAsync("board_sync", new JsonObject
        {
            ["roomId"] = room, ["agentId"] = workerId, ["credential"] = workerKey,
            ["includeSnapshot"] = true
        });
        Assert.True(sync.Success, sync.ErrorMessage);
        var payload = JsonSerializer.SerializeToNode(sync.Data, LocalIdeBridge.Core.AppConfig.JsonOptions)!;
        Assert.Contains(payload["tasks"]!.AsArray(),
            item => item?["title"]?.GetValue<string>() == "Make resolver reliable");

        var handoff = await sb.Host.Registry.InvokeAsync("board_handoff", new JsonObject
        {
            ["roomId"] = room, ["agentId"] = workerId, ["credential"] = workerKey
        });
        Assert.True(handoff.Success, handoff.ErrorMessage);
        var prompt = JsonSerializer.SerializeToNode(handoff.Data,
            LocalIdeBridge.Core.AppConfig.JsonOptions)!["prompt"]!.GetValue<string>();
        Assert.Contains("Make resolver reliable", prompt);
        Assert.Contains("main", prompt);
        Assert.DoesNotContain(workerKey, prompt);
    }

    [Fact]
    public async Task BoardPaginationKeepsOlderMessagesAvailable()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var board = sb.Host.Board;
        var room = board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (agent, key) = board.Join(room, "Lead", "coordinator");

        for (var i = 0; i < 130; i++)
            board.Post(room, agent.Id, key, "message", "Message " + i, "page-" + i);

        var first = board.ReadLatestEvents(room, 30, 0);
        var older = board.ReadLatestEvents(room, 30, 30);
        Assert.Equal(30, first.Count);
        Assert.Equal(30, older.Count);
        Assert.True(first[0].Sequence > older[^1].Sequence);
        Assert.Equal(1L, board.ReadEvents(room, 0, 1).Events[0].Sequence);
    }

    [Fact]
    public async Task BoardWorkRequiresAFileVersionBeforeReplacingExistingContent()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var room = sb.Host.Board.GetOrCreateRoom("VESPER", sb.WorkspaceDir);
        var (agent, key) = sb.Host.Board.Join(room, "Engine", "worker");
        var task = sb.Host.Board.CreateTask(room, agent.Id, key,
            "Edit a file", "Preserve another editor's changes", null, "version-check");
        sb.Host.Board.ChangeTask(room, agent.Id, key, task.Id, "claim", task.Revision);

        Directory.CreateDirectory(Path.Combine(sb.WorkspaceDir, ".git"));
        File.WriteAllText(Path.Combine(sb.WorkspaceDir, ".git", "HEAD"), "ref: refs/heads/main\n");
        var file = Path.Combine(sb.WorkspaceDir, "file.txt");
        await File.WriteAllTextAsync(file, "before\n");

        JsonObject Replace(string? sha)
        {
            var editArgs = new JsonObject
            {
                ["path"] = "file.txt", ["startLine"] = 1, ["endLine"] = 1,
                ["newText"] = "after"
            };
            if (sha is not null) editArgs["expectedSha256"] = sha;
            return new JsonObject
            {
                ["roomId"] = room.Id, ["agentId"] = agent.Id, ["credential"] = key,
                ["taskId"] = task.Id, ["tool"] = "replace_range", ["arguments"] = editArgs
            };
        }

        var missing = await sb.Host.Registry.InvokeAsync("board_work", Replace(null));
        Assert.False(missing.Success);
        Assert.Equal("expected_version_required", missing.ErrorCode);

        var stale = await sb.Host.Registry.InvokeAsync("board_work", Replace(new string('0', 64)));
        Assert.False(stale.Success);
        Assert.Equal("stale_file", stale.ErrorCode);
        Assert.Equal("before\n", await File.ReadAllTextAsync(file));

        var current = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)));
        var changed = await sb.Host.Registry.InvokeAsync("board_work", Replace(current));
        Assert.True(changed.Success, changed.ErrorMessage);
        Assert.StartsWith("after", await File.ReadAllTextAsync(file));
    }

}
