BEGIN TRANSACTION;

-- Basic Table.  This should never change since it controls the schema
-- If the table defining the schema version changes, then we are going
-- to have issues detecting what version the version table is... right?
CREATE TABLE tblREGISTRY (
    'version' INTEGER );

-- Current Version - This is frozen and should change no more, indicating
-- that all future versions should be able to get a list of applications
-- for a particular stream in order to initiate updates.
INSERT INTO tblREGISTRY ('version') VALUES (3);

-- Real Current Schema Version - This is the new version that should now
-- be manipulated in order to detect whether further schema changes and
-- updates are necessary.
CREATE TABLE tblREGISTRY2 (
  'version' INTEGER);
INSERT INTO tblREGISTRY2 ('version') VALUES (4);

-- The backing store for adsk.dls.streamer.registry._Stream
CREATE TABLE tblSTREAM (
    'tag' VARCHAR(10), -- This is a primary key since v3, but not marking as
                       -- such since ALTER TABLE can't make this happen on
                       -- migrate
    'root' TEXT,
    'last_install' timestamp );
-- Create a special stream that tracks items that will qualify for any...
INSERT INTO tblSTREAM  ('tag') VALUES ('_ALL');

-- The backing store for adsk.dls.streamer.registry._Package
CREATE TABLE tblPACKAGES (
    'checksum' VARCHAR(41),
    'stream' VARCHAR(10),
    'install_size' INTEGER,
    'manifest' TEXT,
    CONSTRAINT "PK" PRIMARY KEY ('checksum', 'stream'));

-- The backing store for adsk.dls.streamer.registry._Application
CREATE TABLE tblAPPLICATIONS (
    'uuid' VARCHAR(41),
    'stream' VARCHAR(10),
    'version' VARCHAR(20) NOT NULL,
    'manifest' TEXT,
    CONSTRAINT "PK" PRIMARY KEY ('uuid', 'stream'));
    
-- Tracks an application/package association.  Enables functions like:
--  _Application.packages
--  _Application.uses_package
--  _Package.inclusion_count
CREATE TABLE tblAPPLICATIONPACKAGES (
    'application' VARCHAR(41),
    'package' VARCHAR(41),
    'appstream' VARCHAR(10),
    'pkgstream' VARCHAR(10),
    CONSTRAINT "APP_FK" FOREIGN KEY ('application', 'appstream')
      REFERENCES tblAPPLICATIONS ('uuid', 'stream'),
    CONSTRAINT "PKG_FK" FOREIGN KEY ('package', 'pkgstream')
      REFERENCES tblPACKAGES ('checksum', 'stream')
    );

-- Tracks all the files in a package and where they were written.

CREATE TABLE tblFILES (
    'pkgid' VARCHAR(41),
    'stream' VARCHAR(10),
    'taskid' VARCHAR(32),
    'path' VARCHAR(1024) PRIMARY KEY,
    CONSTRAINT "PKG_FK" FOREIGN KEY ('pkgid', 'stream')
      REFERENCES tblPACKAGES ('checksum', 'stream')
      ON DELETE SET NULL
);

COMMIT TRANSACTION;