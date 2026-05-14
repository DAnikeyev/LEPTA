using LEPTA.Shared.Diagnostics;

namespace LEPTA.Tests;

[TestFixture]
public sealed class ActionLogEventStreamTests
{
    [Test]
    public void Publish_CapsStoredEntriesToConfiguredMaximum()
    {
        var stream = new ActionLogEventStream(maxEntries: 2);

        stream.Publish("Tests", "First");
        stream.Publish("Tests", "Second");
        stream.Publish("Tests", "Third", ActionLogLevel.Warning);

        var entries = stream.GetEntries();
        Assert.That(entries.Select(entry => entry.Message), Is.EqualTo(["Second", "Third"]));
        Assert.That(entries[^1].Level, Is.EqualTo(ActionLogLevel.Warning));
    }

    [Test]
    public void Publish_RaisesEntryPublishedEventWithStoredEntry()
    {
        var stream = new ActionLogEventStream();
        ActionLogEntry? publishedEntry = null;
        stream.EntryPublished += (_, entry) => publishedEntry = entry;

        var createdEntry = stream.Publish("Tests", "Server test completed.");

        Assert.That(publishedEntry, Is.Not.Null);
        Assert.That(publishedEntry!.Id, Is.EqualTo(createdEntry.Id));
        Assert.That(stream.GetEntries().Single().Message, Is.EqualTo("Server test completed."));
    }

    [Test]
    public void Publish_RejectsBlankMessages()
    {
        var stream = new ActionLogEventStream();

        Assert.That(() => stream.Publish("Tests", "   "), Throws.InstanceOf<ArgumentException>());
        Assert.That(stream.GetEntries(), Is.Empty);
    }
}

