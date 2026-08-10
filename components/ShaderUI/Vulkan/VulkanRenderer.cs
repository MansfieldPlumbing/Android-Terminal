using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static Subsystem.VulkanVk;

namespace Subsystem;

public delegate void ExtractExternalHandlesMechanism(
    nint device, 
    ulong sharedMemory, 
    ulong timelineSemaphore, 
    out nint sharedImageHandle, 
    out nint timelineSemaphoreHandle);

public unsafe sealed class VulkanRenderer : IDisposable
{
    private readonly ExtractExternalHandlesMechanism _extractor;

    private nint _instance;
    private nint _physicalDevice;
    private nint _device;

    private ulong _sharedImage;
    private ulong _sharedMemory;
    private ulong _timelineSemaphore;

    public nint SharedImageHandle { get; private set; } = nint.Zero;
    public nint TimelineSemaphoreHandle { get; private set; } = nint.Zero;

    public float iThemeColor_R { get; set; } = 0.0f;
    public float iThemeColor_G { get; set; } = 120.0f / 255.0f;
    public float iThemeColor_B { get; set; } = 215.0f / 255.0f;
    public float iDpiScale { get; set; } = 1.0f;
    public float iCameraOffset_X { get; set; } = 0.0f;
    public float iCameraOffset_Y { get; set; } = 0.0f;

    private float _time = 0.0f;
    private ulong _timelineValue = 0;

    public VulkanRenderer(ExtractExternalHandlesMechanism extractor)
    {
        _extractor = extractor;
    }

    public void BringUp(int width, int height)
    {
        var appInfo = new VkApplicationInfo { sType = VK_STRUCTURE_TYPE_APPLICATION_INFO, apiVersion = 1 << 22 };
        var instanceInfo = new VkInstanceCreateInfo { sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, pApplicationInfo = &appInfo };
        
        nint instance;
        if (vkCreateInstance(&instanceInfo, 0, &instance) != VK_SUCCESS) throw new Exception("Vulkan Instance failed");
        _instance = instance;

        uint devCount = 1;
        nint pDevice;
        vkEnumeratePhysicalDevices(_instance, &devCount, &pDevice);
        _physicalDevice = pDevice;

        float queuePriority = 1.0f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
            queueCount = 1,
            pQueuePriorities = &queuePriority
        };

        var timelineFeatures = new VkPhysicalDeviceTimelineSemaphoreFeatures 
        { 
            sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES, 
            timelineSemaphore = VK_TRUE 
        };

        var deviceFeatures = new VkPhysicalDeviceFeatures2 
        { 
            sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, 
            pNext = (nint)(&timelineFeatures) 
        };

        var deviceInfo = new VkDeviceCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
            pNext = (nint)(&deviceFeatures),
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo
        };

        nint device;
        if (vkCreateDevice(_physicalDevice, &deviceInfo, 0, &device) != VK_SUCCESS) throw new Exception("Vulkan Device failed");
        _device = device;
        
        var extImageInfo = new VkExternalMemoryImageCreateInfo 
        {
            sType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO,
            handleTypes = 0x00000001 | 0x00000008 
        };

        var imageInfo = new VkImageCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            pNext = (nint)(&extImageInfo),
            imageType = 1, 
            format = 37, 
            extent = new VkExtent3D { width = (uint)width, height = (uint)height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = 1, 
            tiling = 0, 
            usage = 0x00000010 | 0x00000002, 
            sharingMode = 0, 
            initialLayout = 0 
        };

        ulong sharedImage;
        vkCreateImage(_device, &imageInfo, 0, &sharedImage);
        _sharedImage = sharedImage;

        VkMemoryRequirements memReq;
        vkGetImageMemoryRequirements(_device, _sharedImage, &memReq);
        
        var extMemInfo = new VkExportMemoryAllocateInfo
        {
            sType = VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO,
            handleTypes = 0x00000001 | 0x00000008 
        };

        var allocInfo = new VkMemoryAllocateInfo
        {
            sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            pNext = (nint)(&extMemInfo),
            allocationSize = memReq.size,
            memoryTypeIndex = FindMemoryType(memReq.memoryTypeBits, 0x00000001) 
        };

        ulong sharedMemory;
        vkAllocateMemory(_device, &allocInfo, 0, &sharedMemory);
        _sharedMemory = sharedMemory;

        vkBindImageMemory(_device, _sharedImage, _sharedMemory, 0);

        var exportSemaphoreInfo = new VkExportSemaphoreCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_EXPORT_SEMAPHORE_CREATE_INFO,
            handleTypes = 0x00000001 | 0x00000008
        };

        var typeSemaphoreInfo = new VkSemaphoreTypeCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_SEMAPHORE_TYPE_CREATE_INFO,
            pNext = (nint)(&exportSemaphoreInfo),
            semaphoreType = 1, 
            initialValue = 0
        };

        var semaphoreInfo = new VkSemaphoreCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
            pNext = (nint)(&typeSemaphoreInfo)
        };

        ulong timelineSemaphore;
        vkCreateSemaphore(_device, &semaphoreInfo, 0, &timelineSemaphore);
        _timelineSemaphore = timelineSemaphore;

        _extractor(
            _device, 
            _sharedMemory, 
            _timelineSemaphore, 
            out nint imgHandle, 
            out nint semHandle);

        SharedImageHandle = imgHandle;
        TimelineSemaphoreHandle = semHandle;
    }
    
    private uint FindMemoryType(uint typeFilter, uint properties)
    {
        VkPhysicalDeviceMemoryProperties memProperties;
        vkGetPhysicalDeviceMemoryProperties(_physicalDevice, &memProperties);

        for (uint i = 0; i < memProperties.memoryTypeCount; i++)
        {
            if ((typeFilter & (1 << (int)i)) != 0 && 
                (memProperties.memoryTypes[i].propertyFlags & properties) == properties)
            {
                return i;
            }
        }
        throw new Exception("Failed to find suitable memory type.");
    }

    public void Render(ReadOnlySpan<UiElementData> elements, float dt)
    {
        _time += dt;
        _timelineValue++;

        var signalInfo = new VkSemaphoreSignalInfo
        {
            sType = VK_STRUCTURE_TYPE_SEMAPHORE_SIGNAL_INFO,
            semaphore = _timelineSemaphore,
            value = _timelineValue
        };
        vkSignalSemaphore(_device, &signalInfo);
    }
    
    public void Resize(int w, int h)
    {
    }

    public void Revoke()
    {
        if (_device != 0)
        {
            if (_timelineSemaphore != 0) vkDestroySemaphore(_device, _timelineSemaphore, 0);
            if (_sharedImage != 0) vkDestroyImage(_device, _sharedImage, 0);
            if (_sharedMemory != 0) vkFreeMemory(_device, _sharedMemory, 0);
            vkDestroyDevice(_device, 0);
        }
        if (_instance != 0)
        {
            vkDestroyInstance(_instance, 0);
        }
    }
    
    public void Dispose() => Revoke();
}
