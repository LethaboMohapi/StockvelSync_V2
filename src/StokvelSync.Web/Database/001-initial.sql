CREATE TABLE IF NOT EXISTS users (
 id CHAR(36) PRIMARY KEY, full_name VARCHAR(100) NOT NULL, phone VARCHAR(16) NOT NULL UNIQUE,
 password_hash VARCHAR(255) NOT NULL, language ENUM('en','zu','xh','af') NOT NULL DEFAULT 'en',
 sms_pin_hash VARCHAR(255), sms_group_id CHAR(36), sms_consent BOOLEAN NOT NULL DEFAULT FALSE,
 created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
);
CREATE TABLE IF NOT EXISTS stokvel_groups (
 id CHAR(36) PRIMARY KEY, name VARCHAR(100) NOT NULL, description VARCHAR(500) NOT NULL DEFAULT '',
 location VARCHAR(100) NOT NULL DEFAULT 'South Africa', contribution_cents INT UNSIGNED NOT NULL,
 due_day TINYINT UNSIGNED NOT NULL DEFAULT 5, join_code VARCHAR(32) NOT NULL UNIQUE,
 created_by CHAR(36) NOT NULL, created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
 CONSTRAINT group_creator FOREIGN KEY (created_by) REFERENCES users(id),
 CONSTRAINT contribution_range CHECK(contribution_cents BETWEEN 100 AND 100000000),
 CONSTRAINT day_range CHECK(due_day BETWEEN 1 AND 28)
);
CREATE TABLE IF NOT EXISTS memberships (
 id CHAR(36) PRIMARY KEY, group_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL,
 role ENUM('treasurer','member') NOT NULL DEFAULT 'member', joined_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
 UNIQUE KEY member_once(group_id,user_id), FOREIGN KEY (group_id) REFERENCES stokvel_groups(id),
 FOREIGN KEY (user_id) REFERENCES users(id)
);
CREATE TABLE IF NOT EXISTS sessions (
 token_hash CHAR(64) PRIMARY KEY, user_id CHAR(36) NOT NULL, expires_at DATETIME(3) NOT NULL,
 FOREIGN KEY(user_id) REFERENCES users(id), INDEX sessions_expiry(expires_at)
);
CREATE TABLE IF NOT EXISTS contributions (
 id CHAR(36) PRIMARY KEY, group_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL,
 amount_cents INT UNSIGNED NOT NULL, period CHAR(7) NOT NULL,
 method ENUM('cash','eft','mobile','sms') NOT NULL, reference VARCHAR(100) NOT NULL DEFAULT '',
 status ENUM('pending','confirmed','rejected') NOT NULL DEFAULT 'pending', reviewed_by CHAR(36),
 created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
 FOREIGN KEY(group_id) REFERENCES stokvel_groups(id), FOREIGN KEY(user_id) REFERENCES users(id),
 FOREIGN KEY(reviewed_by) REFERENCES users(id), INDEX contribution_period(group_id,period),
 CONSTRAINT contribution_amount CHECK(amount_cents BETWEEN 100 AND 100000000)
);
CREATE TABLE IF NOT EXISTS ledger_entries (
 id CHAR(36) PRIMARY KEY, group_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL,
 amount_cents INT NOT NULL, kind ENUM('contribution','payout') NOT NULL,
 source_id CHAR(36) NOT NULL, reference VARCHAR(100) NOT NULL, period CHAR(7) NOT NULL,
 created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
 UNIQUE KEY ledger_source(kind,source_id), INDEX ledger_group(group_id,created_at),
 FOREIGN KEY(group_id) REFERENCES stokvel_groups(id), FOREIGN KEY(user_id) REFERENCES users(id)
);
CREATE TABLE IF NOT EXISTS rotations (
 id CHAR(36) PRIMARY KEY, group_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL,
 payout_date DATE NOT NULL, amount_cents INT UNSIGNED NOT NULL,
 status ENUM('planned','paid') NOT NULL DEFAULT 'planned', paid_reference VARCHAR(100),
 UNIQUE KEY rotation_date(group_id,payout_date),
 FOREIGN KEY(group_id) REFERENCES stokvel_groups(id), FOREIGN KEY(user_id) REFERENCES users(id),
 CONSTRAINT payout_amount CHECK(amount_cents BETWEEN 100 AND 100000000)
);
CREATE TABLE IF NOT EXISTS payment_intents (
 id CHAR(36) PRIMARY KEY, group_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL,
 amount_cents INT UNSIGNED NOT NULL, period CHAR(7) NOT NULL, status ENUM('pending','complete','cancelled') NOT NULL DEFAULT 'pending',
 provider VARCHAR(30) NOT NULL, provider_reference VARCHAR(100) UNIQUE,
 created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
 FOREIGN KEY(group_id) REFERENCES stokvel_groups(id), FOREIGN KEY(user_id) REFERENCES users(id)
);
CREATE TABLE IF NOT EXISTS sync_operations (
 id CHAR(36) PRIMARY KEY, user_id CHAR(36) NOT NULL, group_id CHAR(36) NOT NULL,
 payload_hash CHAR(64) NOT NULL, result JSON NOT NULL,
 created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
 FOREIGN KEY(user_id) REFERENCES users(id), FOREIGN KEY(group_id) REFERENCES stokvel_groups(id)
);
CREATE TABLE IF NOT EXISTS sms_messages (
 id CHAR(36) PRIMARY KEY, group_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL,
 direction ENUM('outbound','inbound') NOT NULL, body VARCHAR(1600) NOT NULL,
 status ENUM('queued','sent','simulated','failed','received','sending') NOT NULL,
 provider_sid VARCHAR(100) UNIQUE, created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
 FOREIGN KEY(group_id) REFERENCES stokvel_groups(id), FOREIGN KEY(user_id) REFERENCES users(id)
);
CREATE TABLE IF NOT EXISTS audit_events (
 id CHAR(36) PRIMARY KEY, group_id CHAR(36) NOT NULL, user_id CHAR(36) NOT NULL,
 action VARCHAR(100) NOT NULL, detail VARCHAR(500) NOT NULL,
 created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
 FOREIGN KEY(group_id) REFERENCES stokvel_groups(id), FOREIGN KEY(user_id) REFERENCES users(id)
);
CREATE TABLE IF NOT EXISTS inbound_receipts (
 sid VARCHAR(100) PRIMARY KEY, response TEXT, created_at TIMESTAMP(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
);
