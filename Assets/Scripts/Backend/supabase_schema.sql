-- ========================================================================
-- AR-NAV Indoor Crowdsourced Navigation Schema (Supabase / PostgreSQL + PostGIS)
-- ========================================================================

-- 1. Enable PostGIS extension for spatial computations and path clustering
CREATE EXTENSION IF NOT EXISTS postgis;

-- 2. Buildings table
CREATE TABLE IF NOT EXISTS buildings (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- 3. Graph Nodes (Anchor-tagged positions & POIs)
CREATE TABLE IF NOT EXISTS graph_nodes (
    id TEXT PRIMARY KEY,
    building_id TEXT REFERENCES buildings(id) ON DELETE CASCADE,
    floor INT NOT NULL DEFAULT 1,
    name TEXT NOT NULL,
    local_x REAL NOT NULL,
    local_y REAL NOT NULL,
    local_z REAL NOT NULL,
    anchor_id TEXT, -- Cloud Anchor ID or local marker reference
    confidence_score REAL DEFAULT 1.0,
    last_verified_at TIMESTAMPTZ DEFAULT NOW(),
    geom GEOMETRY(PointZ, 0) -- 3D geometric point in building local frame
);

-- Index for fast geometric spatial searches
CREATE INDEX IF NOT EXISTS idx_nodes_geom ON graph_nodes USING GIST(geom);
CREATE INDEX IF NOT EXISTS idx_nodes_building ON graph_nodes(building_id, floor);

-- 4. Graph Edges (Crowdsourced walkable paths between nodes)
CREATE TABLE IF NOT EXISTS graph_edges (
    id TEXT PRIMARY KEY,
    building_id TEXT REFERENCES buildings(id) ON DELETE CASCADE,
    floor INT NOT NULL DEFAULT 1,
    node_a TEXT REFERENCES graph_nodes(id) ON DELETE CASCADE,
    node_b TEXT REFERENCES graph_nodes(id) ON DELETE CASCADE,
    path_points JSONB NOT NULL, -- Simplified serialized 3D points [{"x":0,"y":0,"z":0}, ...]
    confidence_score REAL DEFAULT 1.0,
    contributing_walk_count INT DEFAULT 1,
    last_verified_at TIMESTAMPTZ DEFAULT NOW(),
    path_geom GEOMETRY(LineStringZ, 0) -- 3D polyline for PostGIS spatial clustering
);

CREATE INDEX IF NOT EXISTS idx_edges_geom ON graph_edges USING GIST(path_geom);
CREATE INDEX IF NOT EXISTS idx_edges_nodes ON graph_edges(node_a, node_b);

-- 5. Raw Walk Submissions (Crowdsourced audit trail before canonical merging)
CREATE TABLE IF NOT EXISTS raw_walk_submissions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    building_id TEXT REFERENCES buildings(id) ON DELETE CASCADE,
    node_a TEXT REFERENCES graph_nodes(id),
    node_b TEXT REFERENCES graph_nodes(id),
    raw_points JSONB NOT NULL,
    simplified_points JSONB NOT NULL,
    distance_meters REAL,
    duration_seconds REAL,
    walk_rating REAL, -- Thumbs up (+1.0) / down (-0.5)
    submitted_at TIMESTAMPTZ DEFAULT NOW()
);

-- 6. RPC Function: Canonical Edge Merging & Confidence Update
-- Called when a new walk is uploaded: updates walk count and weighted confidence
CREATE OR REPLACE FUNCTION record_walk_submission(
    p_building_id TEXT,
    p_node_a TEXT,
    p_node_b TEXT,
    p_simplified_points JSONB,
    p_rating REAL DEFAULT 1.0
)
RETURNS VOID AS $$
DECLARE
    v_edge_id TEXT;
    v_existing_confidence REAL;
    v_existing_count INT;
BEGIN
    v_edge_id := 'edge_' || p_node_a || '_' || p_node_b;

    SELECT confidence_score, contributing_walk_count 
    INTO v_existing_confidence, v_existing_count
    FROM graph_edges
    WHERE id = v_edge_id OR (node_a = p_node_b AND node_b = p_node_a);

    IF FOUND THEN
        -- Increase confidence if thumbs-up, decrease if thumbs-down; increment count
        UPDATE graph_edges
        SET 
            contributing_walk_count = contributing_walk_count + 1,
            confidence_score = GREATEST(0.1, LEAST(1.0, confidence_score + (p_rating * 0.1))),
            last_verified_at = NOW(),
            path_points = p_simplified_points -- Later can be blended/averaged with PostGIS
        WHERE id = v_edge_id OR (node_a = p_node_b AND node_b = p_node_a);
    ELSE
        -- Create new edge
        INSERT INTO graph_edges (
            id, building_id, node_a, node_b, path_points, confidence_score, contributing_walk_count, last_verified_at
        ) VALUES (
            v_edge_id, p_building_id, p_node_a, p_node_b, p_simplified_points, 1.0, 1, NOW()
        );
    END IF;
END;
$$ LANGUAGE plpgsql;

-- 7. Row-Level Security (RLS) policies for anonymous/public crowd app access
ALTER TABLE buildings ENABLE ROW LEVEL SECURITY;
ALTER TABLE graph_nodes ENABLE ROW LEVEL SECURITY;
ALTER TABLE graph_edges ENABLE ROW LEVEL SECURITY;
ALTER TABLE raw_walk_submissions ENABLE ROW LEVEL SECURITY;

-- Allow reading building graphs publicly
CREATE POLICY "Public read buildings" ON buildings FOR SELECT USING (true);
CREATE POLICY "Public read nodes" ON graph_nodes FOR SELECT USING (true);
CREATE POLICY "Public read edges" ON graph_edges FOR SELECT USING (true);

-- Allow inserting and updating crowdsourced routes anonymously (Required for PostgREST UPSERT resolution=merge-duplicates)
CREATE POLICY "Public insert nodes" ON graph_nodes FOR INSERT WITH CHECK (true);
CREATE POLICY "Public update nodes" ON graph_nodes FOR UPDATE USING (true) WITH CHECK (true);

CREATE POLICY "Public insert edges" ON graph_edges FOR INSERT WITH CHECK (true);
CREATE POLICY "Public update edges" ON graph_edges FOR UPDATE USING (true) WITH CHECK (true);

CREATE POLICY "Public insert raw walks" ON raw_walk_submissions FOR INSERT WITH CHECK (true);

