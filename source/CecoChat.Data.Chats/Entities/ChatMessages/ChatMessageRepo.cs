using Cassandra;
using CecoChat.Contracts.Chats;
using Microsoft.Extensions.Logging;

namespace CecoChat.Data.Chats.Entities.ChatMessages;

public interface IChatMessageRepo : IDisposable
{
    void Prepare();

    Task<IReadOnlyCollection<HistoryMessage>> GetHistory(long userId, string chatId, DateTime olderThan, int countLimit);

    void AddPlainTextMessage(PlainTextMessage message);

    void AddFileMessage(FileMessage message);

    void SetReaction(ReactionMessage message);

    void UnsetReaction(ReactionMessage message);
}

internal sealed class ChatMessageRepo : IChatMessageRepo
{
    private readonly ILogger _logger;
    private readonly IChatMessageTelemetry _chatMessageTelemetry;
    private readonly IChatsDbContext _dbContext;
    private readonly IDataMapper _mapper;
    private readonly Lazy<PreparedStatement> _historyQuery;
    private readonly Lazy<PreparedStatement> _addPlainTextCommand;
    private readonly Lazy<PreparedStatement> _addFileCommand;
    private readonly Lazy<PreparedStatement> _setReactionCommand;
    private readonly Lazy<PreparedStatement> _unsetReactionCommand;

    public ChatMessageRepo(
        ILogger<ChatMessageRepo> logger,
        IChatMessageTelemetry chatMessageTelemetry,
        IChatsDbContext dbContext,
        IDataMapper mapper)
    {
        _logger = logger;
        _chatMessageTelemetry = chatMessageTelemetry;
        _dbContext = dbContext;
        _mapper = mapper;

        _historyQuery = new Lazy<PreparedStatement>(() => _dbContext.PrepareQuery(HistoryQuery));
        _addPlainTextCommand = new Lazy<PreparedStatement>(() => _dbContext.PrepareQuery(AddPlainTextCommand));
        _addFileCommand = new Lazy<PreparedStatement>(() => _dbContext.PrepareQuery(AddFileCommand));
        _setReactionCommand = new Lazy<PreparedStatement>(() => _dbContext.PrepareQuery(SetReactionCommand));
        _unsetReactionCommand = new Lazy<PreparedStatement>(() => _dbContext.PrepareQuery(UnsetReactionCommand));
    }

    public void Dispose()
    {
        _chatMessageTelemetry.Dispose();
    }

    private const string HistoryQuery =
        "SELECT message_id, sender_id, receiver_id, type, text, file, reactions " +
        "FROM chat_messages " +
        "WHERE chat_id = ? AND message_id < ? ORDER BY message_id DESC LIMIT ?";
    private const string AddPlainTextCommand =
        "INSERT INTO chat_messages " +
        "(chat_id, message_id, sender_id, receiver_id, type, text) " +
        "VALUES (?, ?, ?, ?, ?, ?)";
    private const string AddFileCommand =
        "INSERT INTO chat_messages " +
        "(chat_id, message_id, sender_id, receiver_id, type, text, file) " +
        "VALUES (?, ?, ?, ?, ?, ?, ?)";
    private const string SetReactionCommand =
        "UPDATE chat_messages " +
        "SET reactions[?] = ? " +
        "WHERE chat_id = ? AND message_id = ?";
    private const string UnsetReactionCommand =
        "DELETE reactions[?] " +
        "FROM chat_messages " +
        "WHERE chat_id = ? AND message_id = ?";

    public void Prepare()
    {
        _dbContext.PrepareUdt<DbFileData>(_dbContext.Keyspace, "file_data");

        // TODO: do not use Lazy and do not prepare these - just leave fields nullable and mark method that it sets their values
        // TODO: repeat this for the other repo
#pragma warning disable IDE0059
#pragma warning disable IDE1006
        PreparedStatement _ = _historyQuery.Value;
        PreparedStatement __ = _addPlainTextCommand.Value;
        PreparedStatement ___ = _addFileCommand.Value;
        PreparedStatement ____ = _setReactionCommand.Value;
        PreparedStatement _____ = _unsetReactionCommand.Value;
#pragma warning restore IDE0059
#pragma warning restore IDE1006
    }

    public async Task<IReadOnlyCollection<HistoryMessage>> GetHistory(long userId, string chatId, DateTime olderThan, int countLimit)
    {
        long olderThanSnowflake = olderThan.ToSnowflakeCeiling();
        BoundStatement query = _historyQuery.Value.Bind(chatId, olderThanSnowflake, countLimit);
        query.SetConsistencyLevel(ConsistencyLevel.LocalQuorum);
        query.SetIdempotence(true);

        RowSet rows = await _chatMessageTelemetry.GetHistoryAsync(_dbContext.Session, query, userId);
        List<HistoryMessage> messages = new(capacity: countLimit);
        ReadRows(rows, messages);

        _logger.LogTrace("Fetched {MessageCount} messages for chat {Chat} which are older than {OlderThan}", messages.Count, chatId, olderThan);
        return messages;
    }

    private void ReadRows(RowSet rows, ICollection<HistoryMessage> messages)
    {
        foreach (Row row in rows)
        {
            HistoryMessage message = new();

            message.MessageId = row.GetValue<long>("message_id");
            message.SenderId = row.GetValue<long>("sender_id");
            message.ReceiverId = row.GetValue<long>("receiver_id");
            message.Text = row.GetValue<string>("text");

            sbyte messageType = row.GetValue<sbyte>("type");
            message.DataType = _mapper.MapDbToContractDataType(messageType);

            DbFileData dbFile = row.GetValue<DbFileData>("file");
            if (dbFile != null)
            {
                message.File = new HistoryFileData
                {
                    Bucket = dbFile.Bucket,
                    Path = dbFile.Path
                };
            }

            IDictionary<long, string> reactions = row.GetValue<IDictionary<long, string>>("reactions");
            if (reactions != null)
            {
                message.Reactions.Add(reactions);
            }

            messages.Add(message);
        }
    }

    public void AddPlainTextMessage(PlainTextMessage message)
    {
        sbyte dbMessageType = _mapper.MapContractToDbDataType(DataType.PlainText);
        string chatId = DataUtility.CreateChatId(message.SenderId, message.ReceiverId);

        BoundStatement command = _addPlainTextCommand.Value.Bind(
            chatId, message.MessageId, message.SenderId, message.ReceiverId, dbMessageType, message.Text);
        command.SetConsistencyLevel(ConsistencyLevel.LocalQuorum);
        command.SetIdempotence(false);

        _chatMessageTelemetry.AddPlainTextMessage(_dbContext.Session, command, message.MessageId);
        _logger.LogTrace("Persisted plain text message {MessageId} for chat {ChatId}", message.MessageId, chatId);
    }

    public void AddFileMessage(FileMessage message)
    {
        sbyte dbMessageType = _mapper.MapContractToDbDataType(DataType.File);
        string chatId = DataUtility.CreateChatId(message.SenderId, message.ReceiverId);

        DbFileData file = new()
        {
            Bucket = message.Bucket,
            Path = message.Path
        };

        // avoid setting the value to null which would add a tombstone
        string text = message.Text ?? string.Empty;

        BoundStatement command = _addFileCommand.Value.Bind(
            chatId, message.MessageId, message.SenderId, message.ReceiverId, dbMessageType, text, file);
        command.SetConsistencyLevel(ConsistencyLevel.LocalQuorum);
        command.SetIdempotence(false);

        _chatMessageTelemetry.AddFileMessage(_dbContext.Session, command, message.MessageId);
        _logger.LogTrace("Persisted file message {MessageId} for chat {ChatId}", message.MessageId, chatId);
    }

    public void SetReaction(ReactionMessage message)
    {
        string chatId = DataUtility.CreateChatId(message.SenderId, message.ReceiverId);
        BoundStatement command = _setReactionCommand.Value.Bind(message.ReactorId, message.Reaction, chatId, message.MessageId);
        command.SetConsistencyLevel(ConsistencyLevel.LocalQuorum);
        command.SetIdempotence(false);

        _chatMessageTelemetry.SetReaction(_dbContext.Session, command, message.ReactorId);
        _logger.LogTrace("Persisted user {ReactorId} reaction {Reaction} to message {MessageId}", message.ReactorId, message.Reaction, message.MessageId);
    }

    public void UnsetReaction(ReactionMessage message)
    {
        string chatId = DataUtility.CreateChatId(message.SenderId, message.ReceiverId);
        BoundStatement command = _unsetReactionCommand.Value.Bind(message.ReactorId, chatId, message.MessageId);
        command.SetConsistencyLevel(ConsistencyLevel.LocalQuorum);
        command.SetIdempotence(false);

        _chatMessageTelemetry.UnsetReaction(_dbContext.Session, command, message.ReactorId);
        _logger.LogTrace("Persisted user {ReactorId} un-reaction to message {MessageId}", message.ReactorId, message.MessageId);
    }
}
