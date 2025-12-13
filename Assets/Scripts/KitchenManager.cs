using UnityEngine;
using System.Collections.Generic;
using System.Linq;

// --- 1. CORE DATA STRUCTURES (Simplified) ---

/// <summary>
/// Defines a complete food item. Only Veg Biryani for Level 1.
/// </summary>
[System.Serializable]
public class FoodRecipe
{
    public string foodName; // "Veg Biryani"
    public int baseValue = 100; // Base value for this dish
}

/// <summary>
/// Tracks the state of a single cooking instance on the stove.
/// </summary>
public struct CookingSlot
{
    public string status; // "Empty", "Adding Ingredients", "Cooking", "Ready"
    public float timer;
    public Dictionary<string, bool> ingredientsAdded;
    public bool isBusy => status != "Empty";

    public static CookingSlot Empty(string targetFood, List<string> ingredients)
    {
        return new CookingSlot
        {
            status = "Empty",
            timer = 0f,
            // Deep copy the dictionary structure for each slot
            ingredientsAdded = ingredients.ToDictionary(ing => ing, ing => false)
        };
    }
}

// --- NPC STATE ENUMERATOR ---
public enum CustomerState
{
    Empty,        // Spot is vacant
    WalkingIn,    // Moving from Entrance to spot
    Waiting,      // At the spot, waiting for food
    Eating,       // Has food, consuming it (timed)
    WalkingOut    // Moving from spot to Exit
}


// --- 2. GAME MANAGERS AND CONTROLLERS ---

public class KitchenManager : MonoBehaviour
{
    [Header("Player Setup")]
    public float moveSpeed = 5.0f;
    public Rigidbody playerRb; // Assign Player's Rigidbody
    public Transform playerTransform; // Assign Player's Transform
    public float interactionDistance = 2.0f; // How close the player must be to interact

    [Header("Camera Setup")] // Camera settings
    public float smoothSpeed = 0.125f; // Higher value = faster camera follow (less lag)
    private Camera mainCamera;
    private Vector3 cameraOffset; // Stores the distance/angle between camera and player

    [Header("Held Item Visuals (Assign & Parent to Player)")]
    public GameObject heldRiceVisual;
    public GameObject heldVegetablesVisual;
    public GameObject heldSpicesVisual;
    public GameObject heldBiryaniVisual;
    public GameObject heldDirtyPlateVisual; // Player holding a dirty plate

    [Header("Level 1 Stations (Assign in Inspector)")]
    public Transform stovePosition;
    public Transform counterPosition;
    public Transform tablePosition;
    public Transform riceBinPosition;
    public Transform vegBinPosition;
    public Transform spiceBinPosition;
    public Transform dishBinPosition; // The location to drop dirty plates

    // Delivery/Bussing Visuals
    public GameObject counterBiryaniVisual;
    public GameObject tableBiryaniVisual;
    public GameObject tableDirtyPlateVisual; // Plate left on the table


    [Header("NPC & Pathfinding Setup")]
    public GameObject counterCustomerObject;
    public GameObject tableCustomerObject;
    public float customerMoveSpeed = 2.5f;
    public float tableEatingTime = 5.0f;

    public Transform entrancePosition;
    public Transform exitPosition;
    public Transform counterSpotPosition;
    public Transform tableSeatPosition;


    [Header("NPC State")]
    public CustomerState counterCustomerState = CustomerState.Empty;
    public CustomerState tableCustomerState = CustomerState.Empty;
    private float tableEatingTimer = 0f;
    private bool tableHasPlate = false; // Plate left on the table spot


    [Header("Game State")]
    public float gameTime = 0f;
    public float orderInterval = 15f;
    private float timeSinceLastOrder = 0f;
    public List<Order> currentOrders = new List<Order>();

    private List<FoodRecipe> availableRecipes;

    [Header("Player & Cooking State")]
    private string currentHeldItem = "None";

    // Stove State Tracking
    private const string TargetFood = "Veg Biryani";
    private float cookingDuration = 10.0f;
    private List<string> requiredIngredients = new List<string> { "Rice", "Vegetables", "Spices" };
    public List<CookingSlot> stoveSlots = new List<CookingSlot>();
    public int maxStoveSlots = 6;


    // --- Order Structure (Nested for file generation) ---
    [System.Serializable]
    public class Order
    {
        public string requiredFood;
        public float timeLimit;
        public float startTime;
        public bool isTableOrder;
        public FoodRecipe recipe;
        public GameObject customerVisual;
    }

    // --- SETUP & MAIN LOOP ---

    void Start()
    {
        if (playerRb == null) Debug.LogError("Player Rigidbody not assigned!");

        // Camera Setup Initialization
        mainCamera = Camera.main;
        if (mainCamera != null && playerTransform != null)
        {
            cameraOffset = mainCamera.transform.position - playerTransform.position;
        }
        else
        {
            Debug.LogError("Main Camera or Player Transform not found. Camera follow disabled.");
        }

        // Initialize availableRecipes
        availableRecipes = new List<FoodRecipe> { new FoodRecipe { foodName = TargetFood, baseValue = 100 } };

        // Initialize cooking slots
        for (int i = 0; i < maxStoveSlots; i++)
        {
            stoveSlots.Add(CookingSlot.Empty(TargetFood, requiredIngredients));
        }

        // Ensure all visuals start hidden
        UpdateHeldItemVisuals();
        if (counterBiryaniVisual != null) counterBiryaniVisual.SetActive(false);
        if (tableBiryaniVisual != null) tableBiryaniVisual.SetActive(false);
        if (tableDirtyPlateVisual != null) tableDirtyPlateVisual.SetActive(false);

        // Ensure customer visuals are hidden and positioned at entrance initially
        if (counterCustomerObject != null) counterCustomerObject.transform.position = entrancePosition.position;
        if (tableCustomerObject != null) tableCustomerObject.transform.position = entrancePosition.position;
    }

    void Update()
    {
        gameTime += Time.deltaTime;

        HandlePlayerMovement();
        HandlePlayerInteraction(); // Check for 'E' key press near stations
        StoveUpdate(); // Manages all 6 timers

        timeSinceLastOrder += Time.deltaTime;
        if (timeSinceLastOrder >= orderInterval)
        {
            GenerateNewOrder();
            timeSinceLastOrder = 0f;
        }

        CheckOrders();
        HandleCustomerAI();
        UpdateHeldItemVisuals();
    }

    void LateUpdate()
    {
        if (mainCamera == null || playerTransform == null) return;

        Vector3 desiredPosition = playerTransform.position + cameraOffset;
        Vector3 smoothedPosition = Vector3.Lerp(mainCamera.transform.position, desiredPosition, smoothSpeed * Time.deltaTime * 50f);
        mainCamera.transform.position = smoothedPosition;
    }

    // --- PLAYER MOVEMENT (FIXED ROTATION SPINNING ON COLLISION) ---
    private void HandlePlayerMovement()
    {
        if (playerRb == null) return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        Vector3 camForward = Camera.main.transform.forward;
        camForward.y = 0; camForward = camForward.normalized;
        Vector3 camRight = Camera.main.transform.right;

        Vector3 finalDirection = (camRight * h) + (camForward * v);

        if (finalDirection.sqrMagnitude > 1f) finalDirection.Normalize();

        playerRb.linearVelocity = finalDirection * moveSpeed;

        // FIX: Zero out angular velocity to prevent spinning caused by physics collisions
        playerRb.angularVelocity = Vector3.zero;

        // Only update rotation if the player is actively moving (prevents rotation spin on stop)
        if (finalDirection.magnitude > 0.1f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(finalDirection);
            playerTransform.rotation = Quaternion.Slerp(playerTransform.rotation, targetRotation, Time.deltaTime * 10f);
        }
    }

    // --- PLAYER INTERACTION LOGIC ---

    private bool CheckProximity(Transform target)
    {
        if (target == null || playerTransform == null) return false;
        return Vector3.Distance(playerTransform.position, target.position) < interactionDistance;
    }

    private void HandlePlayerInteraction()
    {
        if (Input.GetKeyDown(KeyCode.E))
        {
            // --- 1. Ingredient Bins ---
            if (CheckProximity(riceBinPosition) && riceBinPosition != null) PickUpItem("Rice");
            else if (CheckProximity(vegBinPosition) && vegBinPosition != null) PickUpItem("Vegetables");
            else if (CheckProximity(spiceBinPosition) && spiceBinPosition != null) PickUpItem("Spices");

            // --- 2. Dish Bin (Drop off dirty plates) ---
            else if (CheckProximity(dishBinPosition) && dishBinPosition != null) ProcessInteraction("DishBin");

            // --- 3. Stove (Cook/Collect) ---
            else if (CheckProximity(stovePosition) && stovePosition != null) ProcessInteraction("Stove");

            // --- 4. Table (Pick up plate) ---
            else if (CheckProximity(tablePosition) && tablePosition != null && tableHasPlate) PickUpItem("Dirty Plate");

            // --- 5. Delivery ---
            else if (CheckProximity(counterPosition) && counterPosition != null) DeliverFood(false);
            else if (CheckProximity(tablePosition) && tablePosition != null) DeliverFood(true);

            else
            {
                if (CheckProximity(null) == false)
                {
                    Debug.Log("No interactable object found nearby.");
                }
            }
        }
    }

    private void PickUpItem(string itemName)
    {
        if (currentHeldItem == "None")
        {
            // Handle picking up dirty plate
            if (itemName == "Dirty Plate")
            {
                if (tableHasPlate)
                {
                    tableHasPlate = false;
                    if (tableDirtyPlateVisual != null) tableDirtyPlateVisual.SetActive(false);
                    currentHeldItem = "Dirty Plate";
                    Debug.Log("Picked up: Dirty Plate. Now take it to the Dish Bin!");
                }
                return;
            }

            // Ingredient pickup logic
            if (requiredIngredients.Contains(itemName))
            {
                currentHeldItem = itemName;
                Debug.Log($"Picked up: {itemName}.");
            }
        }
        else
        {
            Debug.Log($"Hands full. Cannot pick up {itemName}. Current item: {currentHeldItem}");
        }
    }

    private void ProcessInteraction(string stationType)
    {
        if (stationType == "DishBin")
        {
            // Dropping dirty plate logic
            if (currentHeldItem == "Dirty Plate")
            {
                currentHeldItem = "None";
                // Optionally increase cleanliness score/XP here
                Debug.Log("Dirty Plate disposed of in the Dish Bin. Good job!");
            }
            else if (currentHeldItem != "None")
            {
                Debug.Log($"Cannot put {currentHeldItem} in the Dish Bin.");
            }
            else
            {
                Debug.Log("Nothing to put in the Dish Bin.");
            }
            return;
        }

        if (stationType == "Stove")
        {
            // 1. COLLECT FINISHED FOOD
            int readySlotIndex = stoveSlots.FindIndex(slot => slot.status == "Ready");
            if (currentHeldItem == "None" && readySlotIndex != -1)
            {
                currentHeldItem = TargetFood + " (Completed)";
                stoveSlots[readySlotIndex] = CookingSlot.Empty(TargetFood, requiredIngredients);
                Debug.Log($"Collected completed {TargetFood} from Slot {readySlotIndex}. Ready to deliver!");
            }
            // 2. ADD INGREDIENT TO SLOT
            else if (requiredIngredients.Contains(currentHeldItem))
            {
                string ingredientToAdd = currentHeldItem;

                int targetSlotIndex = stoveSlots.FindIndex(
                    slot => (slot.status == "Adding Ingredients" || slot.status == "Empty")
                    && slot.ingredientsAdded.ContainsKey(ingredientToAdd)
                    && !slot.ingredientsAdded[ingredientToAdd]
                );

                if (targetSlotIndex == -1)
                {
                    targetSlotIndex = stoveSlots.FindIndex(slot => slot.status == "Empty");
                }

                if (targetSlotIndex != -1)
                {
                    CookingSlot slot = stoveSlots[targetSlotIndex];

                    if (slot.status == "Empty")
                    {
                        slot.status = "Adding Ingredients";
                    }

                    slot.ingredientsAdded[ingredientToAdd] = true;
                    currentHeldItem = "None";

                    int itemsCount = slot.ingredientsAdded.Count(i => i.Value);
                    Debug.Log($"{ingredientToAdd} added to Slot {targetSlotIndex}. Items added: {itemsCount}/{requiredIngredients.Count}");

                    if (itemsCount == requiredIngredients.Count)
                    {
                        slot.status = "Cooking";
                        slot.timer = cookingDuration;
                        Debug.Log($"Slot {targetSlotIndex} complete! Cooking started.");
                    }

                    stoveSlots[targetSlotIndex] = slot;
                }
                else
                {
                    Debug.Log($"Stove is full or no active pots need {ingredientToAdd}.");
                }
            }
            // 3. INTERACTING WITH COOKING/EMPTY STOVE WHILE EMPTY-HANDED (for debug info)
            else if (currentHeldItem == "None" && readySlotIndex == -1)
            {
                Debug.Log("No ready food to collect.");
            }
        }
    }

    // --- COOKING TIMER LOGIC ---
    private void StoveUpdate()
    {
        for (int i = 0; i < stoveSlots.Count; i++)
        {
            CookingSlot slot = stoveSlots[i];

            if (slot.status == "Cooking")
            {
                slot.timer -= Time.deltaTime;

                if (slot.timer <= 0)
                {
                    slot.timer = 0;
                    slot.status = "Ready";
                    Debug.Log($"Ding! Slot {i}: The {TargetFood} is ready!");
                }

                stoveSlots[i] = slot;
            }
        }
    }


    // --- NPC AI & MOVEMENT LOGIC ---

    private void HandleCustomerAI()
    {
        // Counter customer logic
        ProcessCustomer(
            ref counterCustomerState,
            counterCustomerObject,
            counterSpotPosition.position,
            false,
            0f,
            ref tableEatingTimer
        );

        // Table customer logic
        ProcessCustomer(
            ref tableCustomerState,
            tableCustomerObject,
            tableSeatPosition.position,
            true,
            tableEatingTime,
            ref tableEatingTimer
        );
    }

    private void ProcessCustomer(
        ref CustomerState state,
        GameObject customerVisual,
        Vector3 targetSpot,
        bool isTableOrder,
        float maxEatingTime,
        ref float eatingTimer)
    {
        if (customerVisual == null) return;

        switch (state)
        {
            case CustomerState.WalkingIn:
                MoveCustomer(customerVisual, targetSpot, ref state, CustomerState.Waiting);
                break;

            case CustomerState.Waiting:
                break;

            case CustomerState.Eating:
                if (isTableOrder)
                {
                    eatingTimer -= Time.deltaTime;
                    if (eatingTimer <= 0)
                    {
                        // Eating finished, leave plate and start walking out
                        state = CustomerState.WalkingOut;
                        if (tableBiryaniVisual != null) tableBiryaniVisual.SetActive(false);

                        tableHasPlate = true;
                        if (tableDirtyPlateVisual != null) tableDirtyPlateVisual.SetActive(true);
                    }
                }
                break;

            case CustomerState.WalkingOut:
                MoveCustomer(customerVisual, exitPosition.position, ref state, CustomerState.Empty);
                break;

            case CustomerState.Empty:
                customerVisual.SetActive(false);
                break;
        }
    }

    private void MoveCustomer(GameObject customer, Vector3 target, ref CustomerState currentState, CustomerState nextState)
    {
        if (Vector3.Distance(customer.transform.position, target) > 0.1f)
        {
            Vector3 direction = (target - customer.transform.position).normalized;
            customer.transform.position = Vector3.MoveTowards(
                customer.transform.position,
                target,
                customerMoveSpeed * Time.deltaTime
            );

            if (direction.magnitude > 0.01f)
            {
                customer.transform.rotation = Quaternion.Slerp(
                    customer.transform.rotation,
                    Quaternion.LookRotation(direction),
                    Time.deltaTime * 5f
                );
            }
        }
        else
        {
            currentState = nextState;
            if (currentState == CustomerState.Empty)
            {
                customer.transform.position = entrancePosition.position;
            }
        }
    }

    // --- ORDER LOGIC ---

    private void GenerateNewOrder()
    {
        if (availableRecipes == null || availableRecipes.Count == 0) return;

        FoodRecipe requestedRecipe = availableRecipes[0];

        // Table is only free if Empty AND no plate is left behind
        bool counterFree = counterCustomerState == CustomerState.Empty;
        bool tableFree = tableCustomerState == CustomerState.Empty && !tableHasPlate;

        if (!counterFree && !tableFree)
        {
            Debug.Log("Kitchen fully occupied or table needs clearing, cannot take new order.");
            return;
        }

        bool isTableOrder;
        GameObject customerVisual;

        if (counterFree && (currentOrders.Count % 2 == 0 || !tableFree))
        {
            isTableOrder = false;
            customerVisual = counterCustomerObject;
            counterCustomerState = CustomerState.WalkingIn;
        }
        else if (tableFree)
        {
            isTableOrder = true;
            customerVisual = tableCustomerObject;
            tableCustomerState = CustomerState.WalkingIn;
        }
        else
        {
            return;
        }

        if (isTableOrder && tableDirtyPlateVisual != null) tableDirtyPlateVisual.SetActive(false);


        customerVisual.SetActive(true);
        customerVisual.transform.position = entrancePosition.position;

        Order newOrder = new Order
        {
            requiredFood = requestedRecipe.foodName,
            timeLimit = 60f,
            startTime = gameTime,
            isTableOrder = isTableOrder,
            recipe = requestedRecipe,
            customerVisual = customerVisual
        };

        currentOrders.Add(newOrder);
        string location = newOrder.isTableOrder ? "Table" : "Counter";
        Debug.Log($"NEW ORDER! Customer wants {newOrder.requiredFood} at the {location}. Time limit: {newOrder.timeLimit:F0}s.");
    }

    private void DeliverFood(bool deliveredToTable)
    {
        if (currentHeldItem == TargetFood + " (Completed)")
        {
            string deliveredFoodName = TargetFood;

            Order matchingOrder = currentOrders.FirstOrDefault(o =>
                o.requiredFood == deliveredFoodName && o.isTableOrder == deliveredToTable
            );

            if (matchingOrder != null)
            {
                float timeTaken = gameTime - matchingOrder.startTime;
                float timeRemaining = matchingOrder.timeLimit - timeTaken;
                int baseValue = matchingOrder.recipe.baseValue;

                int score = (int)(baseValue * (timeRemaining / matchingOrder.timeLimit) + baseValue);

                Debug.Log($"SUCCESS! Delivered {deliveredFoodName}. Time taken: {timeTaken:F1}s. Score earned: {score}.");

                if (deliveredToTable)
                {
                    if (tableBiryaniVisual != null) tableBiryaniVisual.SetActive(true);
                    tableCustomerState = CustomerState.Eating;
                    tableEatingTimer = tableEatingTime;
                }
                else // Counter
                {
                    if (counterBiryaniVisual != null) counterBiryaniVisual.SetActive(true);
                    counterCustomerState = CustomerState.WalkingOut;
                    if (counterBiryaniVisual != null) counterBiryaniVisual.SetActive(false);
                }

                currentOrders.Remove(matchingOrder);
                currentHeldItem = "None";
            }
            else
            {
                string location = deliveredToTable ? "Table" : "Counter";
                Debug.Log($"Delivered {deliveredFoodName} to the {location}, but there is no customer waiting there for it!");
            }
        }
        else
        {
            Debug.Log($"Cannot deliver: you are not holding a completed dish.");
        }
    }

    private void CheckOrders()
    {
        for (int i = currentOrders.Count - 1; i >= 0; i--)
        {
            Order order = currentOrders[i];
            if (gameTime - order.startTime > order.timeLimit)
            {
                Debug.LogWarning($"ORDER FAILED! Customer for {order.requiredFood} left due to timeout.");

                if (order.isTableOrder)
                {
                    tableCustomerState = CustomerState.WalkingOut;
                }
                else
                {
                    counterCustomerState = CustomerState.WalkingOut;
                }

                currentOrders.RemoveAt(i);
            }
        }
    }

    // --- Toggles visibility of 3D item visuals (player held item) ---
    private void UpdateHeldItemVisuals()
    {
        // Hide all
        if (heldRiceVisual != null) heldRiceVisual.SetActive(false);
        if (heldVegetablesVisual != null) heldVegetablesVisual.SetActive(false);
        if (heldSpicesVisual != null) heldSpicesVisual.SetActive(false);
        if (heldBiryaniVisual != null) heldBiryaniVisual.SetActive(false);
        if (heldDirtyPlateVisual != null) heldDirtyPlateVisual.SetActive(false);

        // Show held item
        switch (currentHeldItem)
        {
            case "Rice":
                if (heldRiceVisual != null) heldRiceVisual.SetActive(true);
                break;
            case "Vegetables":
                if (heldVegetablesVisual != null) heldVegetablesVisual.SetActive(true);
                break;
            case "Spices":
                if (heldSpicesVisual != null) heldSpicesVisual.SetActive(true);
                break;
            case TargetFood + " (Completed)":
                if (heldBiryaniVisual != null) heldBiryaniVisual.SetActive(true);
                break;
            case "Dirty Plate":
                if (heldDirtyPlateVisual != null) heldDirtyPlateVisual.SetActive(true);
                break;
            case "None":
                break;
        }
    }

    // --- VISUAL DEBUGGING (Optional) ---
    void OnGUI()
    {
        // General Info
        GUI.Label(new Rect(10, 10, 300, 20), $"Time: {gameTime:F1}s");

        // Customer Debug Info
        GUI.Label(new Rect(10, 30, 400, 20), $"Counter: {counterCustomerState.ToString()}");
        GUI.Label(new Rect(10, 50, 400, 20), $"Table: {tableCustomerState.ToString()} (Plate: {tableHasPlate} | Eat: {tableEatingTimer:F1}s)");

        // Stove Slot Status
        GUI.Label(new Rect(10, 80, 500, 20), "--- STOVE SLOTS (Capacity: 6) ---");
        for (int i = 0; i < maxStoveSlots; i++)
        {
            CookingSlot slot = stoveSlots[i];
            string status = slot.status;
            string details = "";

            if (status == "Cooking")
            {
                details = $" (Timer: {slot.timer:F1}s)";
            }
            else if (status == "Adding Ingredients" || status == "Ready")
            {
                int added = slot.ingredientsAdded.Count(kv => kv.Value);
                int total = slot.ingredientsAdded.Count;
                details = $" ({added}/{total} ingredients)";
            }

            GUI.Label(new Rect(10, 100 + (i * 20), 500, 20), $"Slot {i}: {status}{details}");
        }


        GUI.Label(new Rect(10, 100 + (maxStoveSlots * 20) + 20, 300, 20), "--- ORDERS ---");
        for (int i = 0; i < currentOrders.Count; i++)
        {
            Order o = currentOrders[i];
            float remaining = o.timeLimit - (gameTime - o.startTime);
            string location = o.isTableOrder ? "Table" : "Counter";
            GUI.Label(new Rect(10, 120 + (maxStoveSlots * 20) + 20 + (i * 20), 400, 20),
                      $"{o.requiredFood} ({location}): {remaining:F1}s left");
        }
    }
}