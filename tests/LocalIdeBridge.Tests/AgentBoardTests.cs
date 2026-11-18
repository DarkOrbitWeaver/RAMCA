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
}
