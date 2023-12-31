using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ComputeUtilities;
using Unity.VisualScripting;
using TMPro;
using UnityEngine.UIElements;
using System.Security.Cryptography;
using System;
using System.IO;

public class SlimeSimulation : MonoBehaviour
{
    // UI Vars
    public RawImage viewport;

    // where 0 = none
    //       1 = circle
    //       2 = big bang
    //       3 = starburst
    public int intializeType = 0;
    public TMP_Dropdown initializeDropdown;

    // where 0 = placing food
    //       1 = placing slime
    //       2 = erasing
    public int brushType = 0;
    public TMP_Dropdown brushDropdown;

    public bool playing = false;
    public TMP_Text togglePlayText;

    public SimulationSettings settings;
    public TMP_InputField brushRadiusField;
    public TMP_InputField trailDecayField;
    public TMP_InputField trailDiffuseField;

    // species handlers
    public TMP_Dropdown speciesDropdown;
    public TMP_InputField sensorAngleField;
    public TMP_InputField rotationAngleField;
    public TMP_InputField sensorDistanceField;
    public TMP_InputField trailWeightField;
    public TMP_InputField velocityField;
    public TMP_InputField hungerDecayField;
    public TMP_InputField rField;
    public TMP_InputField gField;
    public TMP_InputField bField;
    public int activeSpecie = 0;
    private bool changingSpecie = false;

    public ComputeShader computeSim;

    const int updateKernel = 0;
    const int blurKernel = 1;
    const int paintKernel = 2;
    const int foodKernel = 3;
    const int eraseKernel = 4;
    const int clearKernel = 5;

    public RenderTexture viewportTex;
    public RenderTexture trailMap;
    public RenderTexture nextTrailMap;
    public RenderTexture foodMap;

    public List<SlimeAgent> agents = new();
    public HashSet<FoodSource> foodSources = new();
    public SlimeAgent[] agentArray;
    public FoodSource[] foodSourceArray;
    ComputeBuffer agentBuffer;
    ComputeBuffer speciesBuffer;
    ComputeBuffer foodBuffer;

    // Start is called before the first frame update
    void Start()
    {
        SetFields();
        ToggleBrush();

        // Initialize agent, trail, and color buffers
        ComputeUtil.CreateTex(ref viewportTex, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref trailMap, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref nextTrailMap, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref foodMap, settings.vpWidth, settings.vpHeight);

        viewport.texture = viewportTex;

        // update function in compute shader
        computeSim.SetTexture(updateKernel, "TrailMap", trailMap);

        // blur function in compute shader
        computeSim.SetTexture(blurKernel, "TrailMap", trailMap);
        computeSim.SetTexture(blurKernel, "NextTrailMap", nextTrailMap);

        // paint canvas function in compute shader
        computeSim.SetTexture(paintKernel, "ViewportTex", viewportTex);
        computeSim.SetTexture(paintKernel, "TrailMap", trailMap);
        computeSim.SetTexture(paintKernel, "FoodMap", foodMap);

        // paint food function in compute shader
        computeSim.SetTexture(foodKernel, "FoodMap", foodMap);

        // erase function in compute shader
        computeSim.SetTexture(eraseKernel, "TrailMap", trailMap);
        computeSim.SetTexture(eraseKernel, "FoodMap", foodMap);

        // clearing trail, food, and viewport textures (setting to <0,0,0,0>) in compute shader
        ClearAll();

        Initialize();

        SetFood();

        ComputeUtil.CreateBuffer(ref speciesBuffer, settings.species);
        computeSim.SetBuffer(updateKernel, "species", speciesBuffer);
        computeSim.SetBuffer(paintKernel, "species", speciesBuffer);

        computeSim.SetInt("width", settings.vpWidth);
        computeSim.SetInt("height", settings.vpHeight);

        trailDecayField.text = settings.decayRate.ToString("0.000");
        trailDiffuseField.text = settings.diffuseRate.ToString("0.000");

        computeSim.SetInt("foodSourceSize", settings.foodSourceSize);
        computeSim.SetVector("foodColor", settings.foodColor);
        computeSim.SetFloat("cAttraction", settings.foodAttractionCoefficient);
        computeSim.SetBool("foodDepletionEnabled", settings.foodDepletionEnabled);

        computeSim.SetInt("eraseBrushRadius", settings.eraseBrushRadius);

        Simulate();
        Paint();
    }

    void Paint()
    {
        computeSim.SetTexture(paintKernel, "FoodMap", foodMap);
        computeSim.Dispatch(paintKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
    }

    void FixedUpdate()
    {
        if (Input.GetButton("Fire1"))
        {
            switch (brushType)
            {
                case 0:
                    PlaceFood();
                    break;
                case 1:
                    PlaceSlime();
                    break;
                case 2:
                    Erase();
                    break;
            }
        }

        if (playing)
        {
            for (int i = 0; i < settings.simsPerFrame; i++)
            {
                Simulate();
            }
        }

        UpdateFood();
        Paint();
    }

    void Simulate()
    {
        computeSim.SetFloat("dt", Time.fixedDeltaTime);
        computeSim.SetFloat("time", Time.fixedTime);

        // send species related buffers to shader here, for now just using magic values within compute
        if (agents != null && agents.Count > 0)
        {
            computeSim.Dispatch(updateKernel, Mathf.CeilToInt(agentArray.Length / 16.0F), 1, 1);
        }

        computeSim.Dispatch(blurKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        Graphics.Blit(nextTrailMap, trailMap);
    }

    //###########################################################################
    // Food functions 
    //###########################################################################

    public void GetFood()
    {
        if (foodSources.Count > 0)
        {
            foodBuffer.GetData(foodSourceArray);
            foodSources = new HashSet<FoodSource>(foodSourceArray);
        }
    }

    public void SetFood()
    {
        // passing food data + other uniforms
        if (foodSources.Count > 0)
        {
            foodSourceArray = new FoodSource[foodSources.Count];
            foodSources.CopyTo(foodSourceArray);
            ComputeUtil.CreateBuffer(ref foodBuffer, foodSourceArray);
            computeSim.SetBuffer(updateKernel, "foodSources", foodBuffer);
            computeSim.SetBuffer(foodKernel, "foodSources", foodBuffer);
            computeSim.SetInt("numFoodSources", foodSourceArray.Length);
        }
        else
        {
            computeSim.SetInt("numFoodSources", 0);
            FoodSource[] dummy = new FoodSource[1];
            dummy[0] = new FoodSource
            {
                position = new Vector2(0, 0),
                attractorStrength = 0.0F,
                amount = 0
            };
            ComputeUtil.CreateBuffer(ref foodBuffer, dummy);
            computeSim.SetBuffer(updateKernel, "foodSources", foodBuffer);
        }

    }

    void UpdateFood()
    {
        if (foodSources.Count > 0)
        {
            ClearFoodTexture();
            computeSim.Dispatch(foodKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        }
    }

    //###########################################################################
    // Functions for Agent Initialization 
    //###########################################################################

    public void AddAgent(bool randomPos, Vector2 pos)
    {
        // add a singular agent to the end of the agents list 
        // if randomPos, initializes agent at a random position in circle
        // else use specified pos 
        float randomTheta = (float)UnityEngine.Random.value * 2 * Mathf.PI;
        float randomR = (float)UnityEngine.Random.value * (settings.vpHeight / 2 - 50);
        float randomOffsetX = Mathf.Cos(randomTheta) * randomR;
        float randomOffsetY = Mathf.Sin(randomTheta) * randomR;
        float randAngle = Mathf.PI + Mathf.Atan2(randomOffsetY, randomOffsetX);

        Vector2 agentPos = pos;
        if (randomPos)
        {
            agentPos = new Vector2(settings.vpWidth / 2 + randomOffsetX, settings.vpHeight / 2 + randomOffsetY);
        }

        agents.Add(new SlimeAgent
        {
            position = agentPos,
            angle = randAngle,
            speciesID = activeSpecie,
            hunger = 1
        });
    }

    public void SetAgents()
    {
        agentArray = agents.ToArray();

        // passing agent data + other uniforms
        if (agents.Count > 0)
        {
            ComputeUtil.CreateBuffer(ref agentBuffer, agentArray);
            computeSim.SetBuffer(updateKernel, "slimeAgents", agentBuffer);
            computeSim.SetInt("numAgents", agentArray.Length);
        }
    }

    public void CircleAgents()
    {
        // intialize agent positions within circle
        agents = new List<SlimeAgent>(settings.numAgents);
        for (int i = 0; i < settings.numAgents; i++)
        {
            AddAgent(true, new Vector2());
        }

        SetAgents();
    }

    public void BigBang()
    {
        agents = new List<SlimeAgent>(settings.numAgents);
        for (int i = 0; i < settings.numAgents; i++)
        {
            float randomTheta = (float)UnityEngine.Random.value * 2 * Mathf.PI;

            agents.Add(new SlimeAgent
            {
                position = new Vector2(settings.vpWidth / 2, settings.vpHeight / 2),
                angle = randomTheta,
                speciesID = (int)Mathf.Floor(4 * UnityEngine.Random.value),
                hunger = 1
            });
        }

        SetAgents();
    }

    public void Supernova(bool reset, Vector2 center, int numPoints, int radius, float rotation, int numAgents, int speciesID)
    {
        if (reset)
        {
            agents = new List<SlimeAgent>(settings.numAgents);
        }

        float angle = rotation;
        float angleIncrement = 2 * Mathf.PI / (float)numPoints;
        int agentsPerPoint = numAgents / numPoints;

        for (int i = 0; i < numPoints; i++)
        {
            Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (radius / (float)agentsPerPoint);

            for (int j = 0; j < agentsPerPoint; j++)
            {
                Vector2 agentPos = center + (offset * j);

                agents.Add(new SlimeAgent
                {
                    position = agentPos,
                    angle = angle,
                    speciesID = speciesID,
                    hunger = 1
                });
            }

            angle += angleIncrement;
        }

        SetAgents();
    }

    //###########################################################################
    // Functions for UI Button functionality
    //###########################################################################

    public void Initialize()
    {
        intializeType = initializeDropdown.value;
        switch (intializeType)
        {
            case 0:
                ClearAll();
                break;
            case 1:
                CircleAgents();
                break;
            case 2:
                BigBang();
                break;
            case 3:
                Supernova(true, new Vector2(256, 256), 20, 10, 0 * (float)Math.PI / 40F, settings.numAgents / 4, 0);
                Supernova(false, new Vector2(256, 256), 20, 10, 1 * (float)Math.PI / 40F, settings.numAgents / 4, 1);
                Supernova(false, new Vector2(256, 256), 20, 10, 2 * (float)Math.PI / 40F, settings.numAgents / 4, 2);
                Supernova(false, new Vector2(256, 256), 20, 10, 3 * (float)Math.PI / 40F, settings.numAgents / 4, 3);
                break;
        }
    }

    public void TogglePlaying()
    {
        playing = !playing;

        if (playing)
        {
            togglePlayText.SetText("Pause");
        }
        else
        {
            togglePlayText.SetText("Play");
        }

        if ((initializeDropdown.value != intializeType) || (agents.Count == 0))
        {
            Initialize();
        }
    }

    public void ToggleBrush()
    {
        brushType = brushDropdown.value;
        switch (brushType)
        {
            case 0:
                brushRadiusField.text = settings.foodSourceSize.ToString();
                break;
            case 1:
                brushRadiusField.text = settings.slimeBrushRadius.ToString();
                break;
            case 2:
                brushRadiusField.text = settings.eraseBrushRadius.ToString();
                break;
        }
    }

    public void ToggleSpecie()
    {
        activeSpecie = speciesDropdown.value;
        changingSpecie = true;
        SetFields();
        changingSpecie = false;
    }

    public void SaveImage()
    {
        RenderTexture prevTexture = RenderTexture.active;
        RenderTexture.active = viewportTex;

        Texture2D copyTex = new Texture2D(settings.vpWidth, settings.vpHeight);
        copyTex.ReadPixels(new Rect(0, 0, viewportTex.width, viewportTex.height), 0, 0);
        copyTex.Apply();

        RenderTexture.active = prevTexture;

        string path = Application.persistentDataPath + "/" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".png";
        Debug.Log(path);
        byte[] imageData = copyTex.EncodeToPNG();
        File.WriteAllBytes(path, imageData);
    }

    //###########################################################################
    // Changing and updating species characteristics 
    //###########################################################################

    public void SetFields()
    {
        sensorAngleField.text = settings.species[activeSpecie].sensorAngle.ToString("0.000");
        rotationAngleField.text = settings.species[activeSpecie].rotationAngle.ToString("0.000");
        sensorDistanceField.text = settings.species[activeSpecie].sensorDist.ToString("0.000");
        trailWeightField.text = settings.species[activeSpecie].trailWeight.ToString("0.000");
        velocityField.text = settings.species[activeSpecie].velocity.ToString("0.000");
        hungerDecayField.text = settings.species[activeSpecie].hungerDecayRate.ToString("0.000");
        rField.text = settings.species[activeSpecie].color[0].ToString("0.00");
        gField.text = settings.species[activeSpecie].color[1].ToString("0.00");
        bField.text = settings.species[activeSpecie].color[2].ToString("0.00");
    }

    bool ValidField(string field)
    {
        try
        {
            float.Parse(field);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void UpdateRadii()
    {
        if (ValidField(brushRadiusField.text))
        {
            int radius = (int)MathF.Round(float.Parse(brushRadiusField.text));
            switch (brushType)
            {
                case 0:
                    settings.foodSourceSize = radius;
                    computeSim.SetInt("foodSourceSize", settings.foodSourceSize);
                    break;
                case 1:
                    settings.slimeBrushRadius = radius;
                    break;
                case 2:
                    settings.eraseBrushRadius = radius;
                    computeSim.SetInt("eraseBrushRadius", settings.eraseBrushRadius);
                    break;
            }
        }
    }

    public void UpdateDecay()
    {
        if (ValidField(trailDecayField.text))
        {
            settings.decayRate = float.Parse(trailDecayField.text);
            computeSim.SetFloat("decayRate", settings.decayRate);
        }
    }

    public void UpdateDiffuse()
    {
        if (ValidField(trailDiffuseField.text))
        {
            Debug.Log("updated");
            settings.diffuseRate = float.Parse(trailDiffuseField.text);
            computeSim.SetFloat("diffuseRate", settings.diffuseRate);
        }
    }

    public void UpdateSpecie()
    {
        if (ValidField(sensorAngleField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].sensorAngle = float.Parse(sensorAngleField.text);
        }

        if (ValidField(rotationAngleField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].rotationAngle = float.Parse(rotationAngleField.text);
        }

        if (ValidField(sensorDistanceField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].sensorDist = (int)float.Parse(sensorDistanceField.text);
        }

        if (ValidField(trailWeightField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].trailWeight = float.Parse(trailWeightField.text);
        }

        if (ValidField(velocityField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].velocity = float.Parse(velocityField.text);
        }

        if (ValidField(hungerDecayField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].hungerDecayRate = float.Parse(hungerDecayField.text);
        }

        if (ValidField(rField.text) && ValidField(gField.text) && ValidField(bField.text) && !changingSpecie)
        {
            settings.species[activeSpecie].color = new Vector4(
                float.Parse(rField.text),
                float.Parse(gField.text),
                float.Parse(bField.text),
                1.0F
            );
        }

        ComputeUtil.CreateBuffer(ref speciesBuffer, settings.species);
        computeSim.SetBuffer(updateKernel, "species", speciesBuffer);
        computeSim.SetBuffer(paintKernel, "species", speciesBuffer);
    }

    //###########################################################################
    // Brush functionality 
    //###########################################################################

    void PlaceFood()
    {
        // store position of click in screen space
        Vector2 screenPos = new(Input.mousePosition.x, Input.mousePosition.y);
        // convert screen space click position to the coordinate space of the viewport
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport.rectTransform, screenPos, null, out Vector2 canvasPos);
        bool withinCanvas = viewport.rectTransform.rect.Contains(canvasPos);
        // if the click was within the canvas, pass the click position to the compute shader and paint food
        if (withinCanvas)
        {
            Vector2 shiftedCanvasPos = canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2);
            FoodSource newFoodSource = new()
            {
                position = shiftedCanvasPos,
                attractorStrength = settings.foodAttractionCoefficient,
                amount = 1000000,
            };

            if (!foodSources.Contains(newFoodSource))
            {
                // add food source to list of attractors
                GetFood();
                foodSources.Add(newFoodSource);
                SetFood();
            }
        }
    }

    void PlaceSlime()
    {
        // store position of click in screen space
        Vector2 screenPos = new(Input.mousePosition.x, Input.mousePosition.y);

        // convert screen space click position to the coordinate space of the viewport
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport.rectTransform, screenPos, null, out Vector2 canvasPos);
        Vector2 clickPos = canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2);

        bool clickInCanvas = viewport.rectTransform.rect.Contains(canvasPos);

        // get current agent data from the gpu
        if (agents != null && agents.Count > 0)
        {
            agentBuffer.GetData(agentArray);

            // update cpu's current agent data
            for (int i = 0; i < agents.Count; i++)
            {
                agents[i] = agentArray[i];
            }
        }

        // add agents to the cpu's list of agents if user clicked within canvas
        if (clickInCanvas)
        {
            int startX = (int)clickPos[0] - settings.slimeBrushRadius;
            int startY = (int)clickPos[1] - settings.slimeBrushRadius;
            int endX = (int)clickPos[0] + settings.slimeBrushRadius + 1;
            int endY = (int)clickPos[1] + settings.slimeBrushRadius + 1;

            for (int y = startY; y < endY; y++)
            {
                for (int x = startX; x < endX; x++)
                {
                    Vector2 currPos = new Vector2(x, y);
                    bool inRadius = ((currPos - clickPos).magnitude <= settings.slimeBrushRadius);

                    if (inRadius && (UnityEngine.Random.value * 100 < settings.slimeBrushDensity))
                    {
                        AddAgent(false, currPos);
                    }
                }
            }
        }

        SetAgents();

        // if the click was within the canvas, pass the click position to the compute shader
        if (clickInCanvas)
        {
            computeSim.SetVector("clickPos", canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2));
        }

        Paint();
    }

    void Erase()
    {
        // store position of click in screen space
        Vector2 screenPos = new(Input.mousePosition.x, Input.mousePosition.y);

        // convert screen space click position to the coordinate space of the viewport
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport.rectTransform, screenPos, null, out Vector2 canvasPos);
        bool inCanvas = viewport.rectTransform.rect.Contains(canvasPos);

        Vector2 clickPos = canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2);

        // get current agent data from the gpu
        agentBuffer.GetData(agentArray);

        // update cpu's current agent data
        for (int i = 0; i < agents.Count; i++)
        {
            agents[i] = agentArray[i];
        }

        // remove agents from the cpu's list of agents if it is within the erase brush 
        for (int i = 0; i < agents.Count; i++)
        {
            if ((agents[i].position - clickPos).magnitude <= settings.eraseBrushRadius)
            {
                agents.RemoveAt(i);
                i--;
            }
        }

        SetAgents();

        // if the click was within the canvas, pass the click position to the compute shader and erase
        if (inCanvas)
        {
            computeSim.SetVector("clickPos", clickPos);
            computeSim.Dispatch(eraseKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        }

        Paint();
    }

    //###########################################################################
    // Clear functions 
    //###########################################################################

    public void ClearAll()
    {
        computeSim.SetTexture(clearKernel, "ClearTexture", trailMap);
        computeSim.Dispatch(clearKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);

        ClearFood();

        if (agents != null)
        {
            agents.Clear();
            SetAgents();
        }
    }

    public void ClearFood()
    {
        ClearFoodTexture();

        if (foodSources.Count != 0)
        {
            foodSources.Clear();
            SetFood();
        }
    }

    public void ClearFoodTexture()
    {
        computeSim.SetTexture(clearKernel, "ClearTexture", foodMap);
        computeSim.Dispatch(clearKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
    }

    // Called when the attached Object is destroyed.
    void OnDestroy()
    {
        // Release buffers
        agentBuffer.Release();
        speciesBuffer.Release();
    }
}
