# PgHook RabbitMQ Streams

A lightweight containerized service that streams PostgreSQL logical replication events to RabbitMQ Streams in real-time.

## Overview

PgHook captures changes from PostgreSQL using logical replication and forwards them to RabbitMQ Streams for event-driven architecture, data synchronization, and analytics pipelines.

## Quick Start

### 1. Enable logical replication by adding the following to your `postgresql.conf`:

```
wal_level = logical

# If needed, increase the number of WAL senders and replication slots.
# The default is 10 for both.
max_wal_senders = 10
max_replication_slots = 10
```

**Note:** PostgreSQL must be restarted after modifying this setting.

### 2. Create a publication for the tables you want to replicate:

```sql
CREATE PUBLICATION test_publication FOR TABLE table1, table2;
```

### 3. Create RabbitMQ Stream

Use RabbitMQ CLI or management UI to create the target stream (PgHook will not create it automatically).

**Note:** Super Streams are also supported - see optional environment variables below.

### 4. Run PgHook with minimal configuration

```bash
docker run --rm --network=host \
  -e PGH_POSTGRES_CONN="Server=localhost;Username=postgres;Password=postgres;Database=test_db;ApplicationName=PgHook;Trust Server Certificate=true" \
  -e PGH_PUBLICATION_NAMES="test_publication" \
  -e PGH_RMQ_ENDPOINTS="localhost:5552" \
  -e PGH_RMQ_USERNAME="guest" \
  -e PGH_RMQ_PASSWORD="guest" \
  -e PGH_RMQ_STREAM_NAME="test_stream" \
  pghook/rabbitmq-streams
```

## Environment Variables

### Required

| Variable | Description |
|----------|-------------|
| `PGH_POSTGRES_CONN` | PostgreSQL connection string |
| `PGH_PUBLICATION_NAMES` | Comma-separated list of PostgreSQL publications to replicate |
| `PGH_RMQ_ENDPOINTS` | RabbitMQ Stream broker endpoints (format: `host:port` or `host1:port,host2:port`) |
| `PGH_RMQ_USERNAME` | RabbitMQ username |
| `PGH_RMQ_PASSWORD` | RabbitMQ password |
| `PGH_RMQ_STREAM_NAME` | Target RabbitMQ Stream name (must already exist) |

### Optional

| Variable | Default | Description |
|----------|---------|-------------|
| `PGH_USE_PERMANENT_SLOT` | `false` | Create a permanent replication slot (survives service restart) |
| `PGH_REPLICATION_SLOT` | Auto-generated | PostgreSQL replication slot name (required if `PGH_USE_PERMANENT_SLOT=true`) |
| `PGH_RMQ_VHOST` | `/` | RabbitMQ virtual host |
| `PGH_RMQ_IS_SUPER_STREAM` | `false` | Use RabbitMQ super stream for partitioning |
| `PGH_RMQ_PARTITION_KEY_FIELDS_*` | PK fields | Define custom partition keys for super streams (e.g., `PGH_RMQ_PARTITION_KEY_FIELDS_1="schema.table\|column1,column2"`) |

## Persistent Replication Slots

By default, replication slots are temporary and lost when PgHook stops. For stable replication positions across restarts, create a permanent replication slot in PostgreSQL:

```sql
SELECT * FROM pg_create_logical_replication_slot('my_slot', 'pgoutput');
```

Then configure PgHook with both flags:

```bash
-e PGH_USE_PERMANENT_SLOT=true \
-e PGH_REPLICATION_SLOT=my_slot
```

**Note:** `PGH_REPLICATION_SLOT` is required when `PGH_USE_PERMANENT_SLOT=true`.

⚠️ **Important:** Permanent replication slots persist across crashes and prevent removal of required resources. This consumes storage because WAL records and catalog rows required by the slot cannot be removed by VACUUM. In extreme cases, this could cause the database to shut down to prevent transaction ID wraparound.

**Always drop slots that are no longer needed:**

```sql
SELECT * FROM pg_drop_replication_slot('my_slot');
```

## Custom Partition Keys

When using super streams, partition keys determine how messages are distributed across partitions. By default, table primary key fields are used. Override this with optional `PGH_RMQ_PARTITION_KEY_FIELDS_*` variables:

```bash
docker run --network=host --rm \
  -e PGH_POSTGRES_CONN="..." \
  -e PGH_PUBLICATION_NAMES="test_publication" \
  -e PGH_RMQ_ENDPOINTS="localhost:5552" \
  -e PGH_RMQ_USERNAME="guest" \
  -e PGH_RMQ_PASSWORD="guest" \
  -e PGH_RMQ_STREAM_NAME="test_stream" \
  -e PGH_RMQ_IS_SUPER_STREAM=true \
  -e PGH_RMQ_PARTITION_KEY_FIELDS_1="public.table_a|last_name,first_name" \
  -e PGH_RMQ_PARTITION_KEY_FIELDS_2="public.table_b|postal_code,address" \
  pghook/rabbitmq-streams
```

Format: `schema.table|column1,column2` (comma-separated columns for composite partition keys)

## Configuration Examples

### With Permanent Replication Slot

```bash
docker run --rm --network=host \
  -e PGH_POSTGRES_CONN="Server=localhost;Username=postgres;Password=postgres;Database=test_db;ApplicationName=PgHook;Trust Server Certificate=true" \
  -e PGH_PUBLICATION_NAMES="test_publication" \
  -e PGH_REPLICATION_SLOT="test_slot" \
  -e PGH_USE_PERMANENT_SLOT=true \
  -e PGH_RMQ_ENDPOINTS="localhost:5552" \
  -e PGH_RMQ_USERNAME="guest" \
  -e PGH_RMQ_PASSWORD="guest" \
  -e PGH_RMQ_STREAM_NAME="test_stream" \
  pghook/rabbitmq-streams
```

### With Super Stream Partitioning

```bash
docker run --rm --network=host \
  -e PGH_POSTGRES_CONN="Server=localhost;Username=postgres;Password=postgres;Database=test_db;ApplicationName=PgHook;Trust Server Certificate=true" \
  -e PGH_PUBLICATION_NAMES="test_publication" \
  -e PGH_RMQ_ENDPOINTS="localhost:5552" \
  -e PGH_RMQ_USERNAME="guest" \
  -e PGH_RMQ_PASSWORD="guest" \
  -e PGH_RMQ_STREAM_NAME="test_stream" \
  -e PGH_RMQ_IS_SUPER_STREAM=true \
  -e PGH_RMQ_PARTITION_KEY_FIELDS_1="public.test_table|last_name" \
  pghook/rabbitmq-streams
```

## How It Works

1. Connects to PostgreSQL using the provided connection string
2. Subscribes to specified publications
3. Creates or reuses a replication slot to capture changes
4. Streams all database changes (INSERT, UPDATE, DELETE) to RabbitMQ Streams
5. Supports partitioning via super streams for distributed processing

## Notes

- Use `--network=host` for local development; adjust for production networking

## Troubleshooting

- Ensure PostgreSQL user has replication privileges
- Verify publication exists and contains desired tables
- Check RabbitMQ connectivity and credentials
- Verify `wal_level = logical` is set and PostgreSQL is restarted
- Review container logs for detailed error messages
