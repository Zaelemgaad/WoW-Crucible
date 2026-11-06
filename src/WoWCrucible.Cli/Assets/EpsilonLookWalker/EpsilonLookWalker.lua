-- Steps through every customization choice at the barber, one per delay, so the client streams each
-- choice's files into its local store. It only previews: it never applies a look, and resets on finish.
--   /lookwalk         start, or resume where it stopped
--   /lookwalk stop    stop and reset the preview
--   /lookwalk reset   forget the saved place
-- The barber's models stream a file when they draw it, so leave the screen open and the game in front.
EpsilonLookWalkerDB = EpsilonLookWalkerDB or {}

local DELAY = 0.6
local queue, position, ticker

local function say(text)
    print("|cff33ccffLookWalk|r " .. text)
end

local function placeKey()
    local _, _, race = UnitRace("player")
    local data = C_BarberShop.GetCurrentCharacterData()
    return race .. "-" .. tostring(data and data.sex)
end

-- Every (option, choice) pair, then every pair again under each other choice of a body type option,
-- as fur and similar layers only draw under the body that names them.
local function buildQueue()
    local pairsToVisit, bodies = {}, {}
    for _, category in ipairs(C_BarberShop.GetAvailableCustomizations() or {}) do
        for _, option in ipairs(category.options or {}) do
            for _, choice in ipairs(option.choices or {}) do
                pairsToVisit[#pairsToVisit + 1] = { option.id, choice.id }
            end
            if option.name == "Body Type" then
                for index = 2, #option.choices do
                    bodies[#bodies + 1] = { option.id, option.choices[index].id }
                end
            end
        end
    end
    local all = {}
    for _, pair in ipairs(pairsToVisit) do
        all[#all + 1] = { pair[1], pair[2] }
    end
    for _, body in ipairs(bodies) do
        for _, pair in ipairs(pairsToVisit) do
            if pair[1] ~= body[1] then
                all[#all + 1] = { pair[1], pair[2], body[1], body[2] }
            end
        end
    end
    return all
end

local function finish(text)
    if ticker then
        ticker:Cancel()
        ticker = nil
    end
    C_BarberShop.ResetCustomizationChoices()
    say(text)
end

local function step()
    local pair = queue[position]
    if not pair then
        EpsilonLookWalkerDB[placeKey()] = nil
        finish("done. Press Esc to leave the barber, then exit the game normally so the store saves.")
        return
    end
    if pair[3] then
        C_BarberShop.SetCustomizationChoice(pair[3], pair[4])
    end
    C_BarberShop.SetCustomizationChoice(pair[1], pair[2])
    position = position + 1
    EpsilonLookWalkerDB[placeKey()] = position
    if position % 100 == 0 then
        say(position .. " of " .. #queue)
    end
end

local function start()
    if ticker then
        say("already running, " .. position .. " of " .. #queue)
        return
    end
    if not (BarberShopFrame and BarberShopFrame:IsShown()) and not C_BarberShop.GetCurrentCharacterData() then
        say("open the barber first: sit in a chair, or use your server's barber command.")
        return
    end
    queue = buildQueue()
    position = EpsilonLookWalkerDB[placeKey()] or 1
    say("walking " .. #queue .. " choices from " .. position .. ", about " ..
        math.ceil((#queue - position) * DELAY / 60) .. " minutes.")
    ticker = C_Timer.NewTicker(DELAY, step)
end

SLASH_LOOKWALK1 = "/lookwalk"
SlashCmdList.LOOKWALK = function(argument)
    if argument == "stop" then
        if ticker then
            finish("stopped at " .. position .. ". /lookwalk resumes.")
        end
    elseif argument == "reset" then
        EpsilonLookWalkerDB = {}
        say("saved places cleared.")
    else
        start()
    end
end

local events = CreateFrame("Frame")
events:RegisterEvent("BARBER_SHOP_CLOSE")
events:SetScript("OnEvent", function()
    if ticker then
        ticker:Cancel()
        ticker = nil
        say("barber closed at " .. position .. ". /lookwalk resumes.")
    end
end)
