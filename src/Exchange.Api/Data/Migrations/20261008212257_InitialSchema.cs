using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Exchange.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "banks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    markup_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_banks", x => x.id);
                    table.CheckConstraint("ck_banks_channel_known", "channel IN ('ATM', 'WIRE')");
                    table.CheckConstraint("ck_banks_markup_not_negative", "markup_percent >= 0");
                });

            migrationBuilder.CreateTable(
                name: "cities",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    markup_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cities", x => x.id);
                    table.CheckConstraint("ck_cities_markup_not_negative", "markup_percent >= 0");
                });

            migrationBuilder.CreateTable(
                name: "usdt_networks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    fee_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usdt_networks", x => x.id);
                    table.CheckConstraint("ck_usdt_networks_fee_not_negative", "fee_percent >= 0");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    telegram_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    first_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    loyalty_points = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_loyalty_points_range", "loyalty_points BETWEEN 0 AND 10");
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    give_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    receive_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount_give = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    amount_receive = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    rate_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rate_snapshot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    base_rate = table.Column<decimal>(type: "numeric(24,12)", precision: 24, scale: 12, nullable: false),
                    city_markup_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    bank_give_markup_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    bank_receive_markup_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    network_give_fee_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    network_receive_fee_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    loyalty_discount_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    effective_rate = table.Column<decimal>(type: "numeric(24,12)", precision: 24, scale: 12, nullable: false),
                    city_id = table.Column<int>(type: "integer", nullable: true),
                    bank_give_id = table.Column<int>(type: "integer", nullable: true),
                    bank_receive_id = table.Column<int>(type: "integer", nullable: true),
                    network_give_id = table.Column<int>(type: "integer", nullable: true),
                    network_receive_id = table.Column<int>(type: "integer", nullable: true),
                    usdt_transfer_time = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    iban = table.Column<byte[]>(type: "bytea", nullable: true),
                    recipient_name = table.Column<byte[]>(type: "bytea", nullable: true),
                    wallet_address = table.Column<byte[]>(type: "bytea", nullable: true),
                    payment_purpose = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pii_purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_amount_give_positive", "amount_give > 0");
                    table.CheckConstraint("ck_orders_amount_receive_positive", "amount_receive > 0");
                    table.CheckConstraint("ck_orders_base_rate_positive", "base_rate > 0");
                    table.CheckConstraint("ck_orders_effective_rate_positive", "effective_rate > 0");
                    table.CheckConstraint("ck_orders_give_code_known", "give_code IN ('EUR_CASH', 'RUB_WIRE', 'USDT', 'USD_CASH')");
                    table.CheckConstraint("ck_orders_receive_code_known", "receive_code IN ('EUR_ATM', 'EUR_CASH', 'EUR_INVOICE', 'EUR_WIRE', 'USDT')");
                    table.CheckConstraint("ck_orders_status_known", "status IN ('CANCELLED', 'COMPLETED', 'CONFIRMED', 'PENDING', 'PROCESSING')");
                    table.CheckConstraint("ck_orders_usdt_transfer_time_known", "usdt_transfer_time IN ('at_meeting', 'now')");
                    table.ForeignKey(
                        name: "fk_orders_bank_give_id",
                        column: x => x.bank_give_id,
                        principalTable: "banks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_bank_receive_id",
                        column: x => x.bank_receive_id,
                        principalTable: "banks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_city_id",
                        column: x => x.city_id,
                        principalTable: "cities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_network_give_id",
                        column: x => x.network_give_id,
                        principalTable: "usdt_networks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_network_receive_id",
                        column: x => x.network_receive_id,
                        principalTable: "usdt_networks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_orders_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order_id = table.Column<int>(type: "integer", nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_status_history", x => x.id);
                    table.CheckConstraint("ck_order_status_history_actor_known", "actor IN ('admin', 'client', 'operator', 'system')");
                    table.CheckConstraint("ck_order_status_history_from_status_known", "from_status IN ('CANCELLED', 'COMPLETED', 'CONFIRMED', 'PENDING', 'PROCESSING')");
                    table.CheckConstraint("ck_order_status_history_to_status_known", "to_status IN ('CANCELLED', 'COMPLETED', 'CONFIRMED', 'PENDING', 'PROCESSING')");
                    table.ForeignKey(
                        name: "fk_order_status_history_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_banks_code_channel",
                table: "banks",
                columns: new[] { "code", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cities_name",
                table: "cities",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_order_id",
                table: "order_status_history",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_bank_give_id",
                table: "orders",
                column: "bank_give_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_bank_receive_id",
                table: "orders",
                column: "bank_receive_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_city_id",
                table: "orders",
                column: "city_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_network_give_id",
                table: "orders",
                column: "network_give_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_network_receive_id",
                table: "orders",
                column: "network_receive_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_id",
                table: "orders",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_usdt_networks_code",
                table: "usdt_networks",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_telegram_id",
                table: "users",
                column: "telegram_id",
                unique: true);

            // Reference data of spec 1.2 (NR-2: the office's current prices, awaiting review).
            // Ids are left to the identity columns, so later inserts never collide with the seed.
            migrationBuilder.Sql("""
                INSERT INTO cities (name, country, markup_percent) VALUES
                    ('Podgorica', 'ME', 0.0), ('Budva', 'ME', 0.5), ('Bar', 'ME', 0.5),
                    ('Tivat', 'ME', 0.7), ('Kotor', 'ME', 0.7), ('Herceg Novi', 'ME', 1.0);
                INSERT INTO banks (code, channel, currency, country, markup_percent) VALUES
                    ('SBER', 'WIRE', 'RUB', 'RU', 0.0), ('TINKOFF', 'WIRE', 'RUB', 'RU', 0.5),
                    ('LOVCEN', 'ATM', 'EUR', 'ME', 0.0), ('CKB', 'ATM', 'EUR', 'ME', 0.0),
                    ('ERSTE', 'ATM', 'EUR', 'ME', 0.5);
                INSERT INTO usdt_networks (code, fee_percent) VALUES
                    ('TRC20', 0.3), ('TON', 0.4), ('ERC20', 1.0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_status_history");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "banks");

            migrationBuilder.DropTable(
                name: "cities");

            migrationBuilder.DropTable(
                name: "usdt_networks");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
