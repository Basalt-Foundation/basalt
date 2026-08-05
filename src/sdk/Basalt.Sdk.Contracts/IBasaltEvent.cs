using Basalt.Core;

namespace Basalt.Sdk.Contracts;

/// <summary>
/// An event that can put itself on chain.
///
/// Implemented for you: the source generator writes both members for every type marked
/// <see cref="BasaltEventAttribute"/>, from the properties it already reads to build the ABI. Declaring
/// the event is the whole job.
///
/// It exists as a constraint on <see cref="Context.Emit{TEvent}"/> rather than as a convention, because
/// the failure it replaces was silent. The runtime used to receive the event object and have no way to
/// read it without reflection, which the trim and AOT analysers forbid, so it logged the type name and
/// dropped every field. Nothing failed, and a transfer recorded that a transfer had happened without
/// saying who received what. As a constraint, an event that cannot encode itself is a compile error at
/// the line that emits it.
/// </summary>
public interface IBasaltEvent
{
    /// <summary>
    /// Every property, in declaration order, encoded as the receipt records it.
    ///
    /// Indexed properties appear here too. Leaving them out would save a few bytes and cost the log its
    /// ability to be read without a second lookup, and the topics are an index, not the record.
    /// </summary>
    byte[] ToLogData();

    /// <summary>
    /// One topic per <see cref="IndexedAttribute"/> property, in declaration order.
    ///
    /// Each is the hash of that property's encoding rather than the value itself. It makes every type
    /// behave the same way, including the ones too long to fit, and a caller filtering on a value hashes
    /// it the same way to get the topic to match against.
    /// </summary>
    Hash256[] ToLogTopics();
}
