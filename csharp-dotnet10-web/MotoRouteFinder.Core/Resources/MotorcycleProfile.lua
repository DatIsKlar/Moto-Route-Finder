name = 'motorcycle'
vehicle_types = { 'vehicle', 'motor_vehicle', 'motorcycle' }

minspeed = 30
maxspeed = 200

speed_profile = {
    ['motorway'] = 120,
    ['motorway_link'] = 120,
    ['trunk'] = 100,
    ['trunk_link'] = 100,
    ['primary'] = 90,
    ['primary_link'] = 90,
    ['secondary'] = 80,
    ['secondary_link'] = 80,
    ['tertiary'] = 70,
    ['tertiary_link'] = 70,
    ['unclassified'] = 50,
    ['residential'] = 20,
    ['service'] = 15,
    ['road'] = 30,
    ['track'] = 5,
    ['living_street'] = 10,
    ['path'] = 5,
    ['footway'] = 0,
    ['cycleway'] = 0,
    ['pedestrian'] = 0,
    ['steps'] = 0,
    ['default'] = 10
}

access_values = {
    ['private'] = false,
    ['yes'] = true,
    ['no'] = false,
    ['permissive'] = true,
    ['destination'] = true,
    ['customers'] = false,
    ['designated'] = true,
    ['public'] = true,
    ['delivery'] = true,
}

profile_whitelist = {
    'highway',
    'oneway',
    'motor_vehicle',
    'vehicle',
    'access',
    'maxspeed',
    'junction',
    'barrier',
    'surface',
    'tracktype',
}

meta_whitelist = {
    'name',
    'bridge',
    'tunnel'
}

profiles = {
    { name = '', function_name = 'factor_and_speed', metric = 'time' },
    { name = 'shortest', function_name = 'factor_and_speed', metric = 'distance' },
}

function factor_and_speed(attributes, result)
    local highway = attributes.highway
    result.speed = 0
    result.direction = 0
    result.canstop = true
    result.attributes_to_keep = {}

    local route = attributes.route
    if route == 'ferry' then
        highway = 'ferry'
        result.attributes_to_keep.route = highway
    end

    local highway_speed = speed_profile[highway]
    if highway_speed then
        result.speed = highway_speed
        result.direction = 0
        result.canstop = true
        result.attributes_to_keep.highway = highway
    else
        return
    end

    local surface = attributes.surface
    if surface == 'unpaved' or surface == 'gravel' or
       surface == 'dirt' or surface == 'sand' or
       surface == 'ground' or surface == 'earth' then
        result.speed = result.speed * 0.2
        result.attributes_to_keep.surface = true
    elseif surface == 'compacted' or surface == 'fine_gravel' then
        result.speed = result.speed * 0.5
        result.attributes_to_keep.surface = true
    end

    local tracktype = attributes.tracktype
    if tracktype == 'grade2' then
        result.speed = result.speed * 0.6
        result.attributes_to_keep.tracktype = true
    elseif tracktype == 'grade3' then
        result.speed = result.speed * 0.3
        result.attributes_to_keep.tracktype = true
    elseif tracktype == 'grade4' then
        result.speed = result.speed * 0.15
        result.attributes_to_keep.tracktype = true
    elseif tracktype == 'grade5' then
        result.speed = result.speed * 0.05
        result.attributes_to_keep.tracktype = true
    end

    local motor_vehicle = attributes.motor_vehicle
    if motor_vehicle == 'no' then return end
    local vehicle = attributes.vehicle
    if vehicle == 'no' then return end
    local access = attributes.access
    if access == 'private' or access == 'no' then return end

    local oneway = attributes.oneway
    if oneway == 'yes' or oneway == '1' then
        result.direction = 1
    elseif oneway == '-1' then
        result.direction = -1
    end

    local junction = attributes.junction
    if junction == 'roundabout' then
        result.direction = 1
        result.attributes_to_keep.junction = true
    end
end
